using Finbuckle.MultiTenant;
using FSH.Framework.Shared.Multitenancy;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace FSH.Modules.PAF.Saude;

/// <summary>
/// Torna tolerável a cópia do tenant no cache distribuído quando o Valkey está fora (S14, seção 9). O kit grava o
/// tenant resolvido pelo banco no <c>DistributedCacheStore</c> em <c>OnTenantResolveCompleted</c>
/// (<c>MultitenancyModule.cs:88-99</c>) sem tratar falha, e com o Valkey fora toda requisição com tenant morre em
/// 500 antes de chegar ao PAF, o que tornaria o modo degradado inalcançável. A leitura do mesmo cache já é
/// tolerante no Finbuckle (registra e segue para o banco); só a gravação não é.
/// </summary>
internal static partial class CacheDeTenantSemValkey
{
    // What a request waits for the cache copy; it is a copy, so past this it goes on in the background.
    private static readonly TimeSpan PrazoCopia = TimeSpan.FromMilliseconds(100);

    // ponytail: a PostConfigure over the kit's event, because MultitenancyModule.cs is outside ADR 0008. Ceiling:
    // while the Valkey is down each request still pays Finbuckle's cache read (the asyncTimeout) plus PrazoCopia.
    // Upgrade path: the same guard in the kit (upstream), then delete this file and its line in PafModule.
    public static void Registrar(IServiceCollection services) =>
        services.PostConfigure<MultiTenantOptions<AppTenantInfo>>(opcoes =>
        {
            var doKit = opcoes.Events.OnTenantResolveCompleted;
            if (doKit is null)
            {
                return;
            }

            // Outside a request (no logger to reach) the kit's behaviour stays as it is.
            opcoes.Events.OnTenantResolveCompleted = contexto =>
                contexto.Context is HttpContext http
                    ? CopiarSemSegurarAsync(doKit(contexto), http.RequestServices.GetRequiredService<ILogger<MultiTenantOptions<AppTenantInfo>>>())
                    : doKit(contexto);
        });

    // The tenant is already resolved from the database; only its cache copy can be lost.
    private static async Task CopiarSemSegurarAsync(Task copia, ILogger logger)
    {
        try
        {
            await copia.WaitAsync(PrazoCopia).ConfigureAwait(false);
        }
        catch (TimeoutException) when (!copia.IsCompleted)
        {
            _ = ObservarAsync(copia, logger);
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            LogCopiaPerdida(logger, ex);
        }
    }

    // Never faults: the discarded copy must not become an unobserved exception.
    private static async Task ObservarAsync(Task copia, ILogger logger)
    {
        try
        {
            await copia.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogCopiaPerdida(logger, ex);
        }
    }

    [LoggerMessage(EventId = 9140, Level = LogLevel.Warning,
        Message = "Gravacao do tenant no cache distribuido falhou (Valkey indisponivel?); segue resolvido pelo banco")]
    private static partial void LogCopiaPerdida(ILogger logger, Exception ex);
}
