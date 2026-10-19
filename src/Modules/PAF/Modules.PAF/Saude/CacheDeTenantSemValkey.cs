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
    // ponytail: a PostConfigure over the kit's event, because MultitenancyModule.cs is outside ADR 0008. Ceiling:
    // each request pays the Valkey asyncTimeout twice (Finbuckle's read and this write) while it is down. Upgrade
    // path: the same try/catch in the kit (upstream), then delete this file and its line in PafModule.
    public static void Registrar(IServiceCollection services) =>
        services.PostConfigure<MultiTenantOptions<AppTenantInfo>>(opcoes =>
        {
            var doKit = opcoes.Events.OnTenantResolveCompleted;
            if (doKit is null)
            {
                return;
            }

            opcoes.Events.OnTenantResolveCompleted = async contexto =>
            {
                try
                {
                    await doKit(contexto).ConfigureAwait(false);
                }
                // The tenant is already resolved from the database; only its cache copy is lost. Outside a request
                // (no logger to reach) the failure propagates as before.
                catch (Exception ex) when (ex is RedisException or TimeoutException && contexto.Context is HttpContext)
                {
                    var http = (HttpContext)contexto.Context;
                    LogCopiaPerdida(http.RequestServices.GetRequiredService<ILogger<MultiTenantOptions<AppTenantInfo>>>(), ex);
                }
            };
        });

    [LoggerMessage(EventId = 9140, Level = LogLevel.Warning,
        Message = "Valkey indisponivel ao gravar o tenant no cache distribuido; segue resolvido pelo banco (modo degradado)")]
    private static partial void LogCopiaPerdida(ILogger logger, Exception ex);
}
