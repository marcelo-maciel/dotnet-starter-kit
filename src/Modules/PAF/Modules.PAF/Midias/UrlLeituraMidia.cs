using Finbuckle.MultiTenant.Abstractions;
using FSH.Framework.Core.Context;
using FSH.Framework.Core.Exceptions;
using FSH.Framework.Shared.Multitenancy;
using FSH.Modules.PAF.Contracts.v1.Midias;
using FSH.Modules.PAF.Data;
using FSH.Modules.PAF.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace FSH.Modules.PAF.Midias;

/// <summary>
/// Signed read URL of a mídia (M04, RF03, ADR 0007). The caller passes a mídia it already read through the unit filter,
/// so the unidade check is the read itself; this class adds the state check (active, envio confirmed), the validity
/// and a short HybridCache, because every new URL is a new address for the browser cache and the panel would download
/// the whole video again on every loop.
/// </summary>
public sealed partial class UrlLeituraMidia(
    PafDbContext db,
    ArmazenamentoMidia armazenamento,
    HybridCache cache,
    IRequestContext requisicao,
    IMultiTenantContextAccessor<AppTenantInfo> tenantAccessor,
    TimeProvider relogio,
    IOptions<OpcoesMidia> opcoes,
    ILogger<UrlLeituraMidia> logger)
{
    // The cache only spares a signature: past this deadline (Valkey stalled) the URL is signed without it, so a stalled
    // cache never holds the panel. Above the Valkey asyncTimeout of the environments (500 ms, PafHostValkey.PrazosValkey).
    private static readonly TimeSpan PrazoCache = TimeSpan.FromSeconds(1);

    private static readonly TimeSpan ArredondamentoDaAssinatura = TimeSpan.FromSeconds(2);

    /// <summary>
    /// The 7-day R2 cap minus the 15-minute clock skew S3 tolerates. The AWS SDK signs with its clock corrected to the
    /// store's, so a URL asked for exactly 7 days can come out a few seconds over 604800 s, and above that the SDK
    /// silently switches to SigV2 (AWSAccessKeyId/Expires/Signature, seen against RustFS), which R2 does not accept.
    /// A skew past 15 minutes fails every S3 request anyway, so this slack covers every skew that can sign at all.
    /// </summary>
    public static readonly TimeSpan TetoValidade = OpcoesMidia.ValidadeMaximaAssinatura - TimeSpan.FromMinutes(15);

    private readonly OpcoesMidia _opcoes = opcoes.Value;

    /// <summary>
    /// URL and expiry for <paramref name="midia"/>, valid for at least <paramref name="duracaoTotal"/> (the longest
    /// uninterrupted exhibition the caller will run with it: duration × repetitions) plus the margin, never below the
    /// floor nor above <see cref="TetoValidade"/>. Null when the mídia is inactive or its current envio is not confirmed: such a mídia gets
    /// no URL.
    /// </summary>
    public async Task<UrlLeituraMidiaDto?> GerarAsync(Midia midia, TimeSpan duracaoTotal, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(midia);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(duracaoTotal, TimeSpan.Zero);
        if (!midia.Ativa || !await EnvioConfirmadoAsync(midia, ct).ConfigureAwait(false))
        {
            return null;
        }

        var validade = Validade(duracaoTotal, _opcoes);
        var gerada = await DoCacheOuAssinarAsync(midia, validade, DuracaoCache(validade, duracaoTotal, _opcoes), ct).ConfigureAwait(false);
        return new UrlLeituraMidiaDto(Resolver(gerada.Url), gerada.ExpiraEm);
    }

    /// <summary>Exhibition + margin, at least the floor, at most <see cref="TetoValidade"/> (R2 cap, ADR 0007).</summary>
    public static TimeSpan Validade(TimeSpan duracaoTotal, OpcoesMidia opcoes)
    {
        ArgumentNullException.ThrowIfNull(opcoes);
        var pedida = duracaoTotal + opcoes.MargemLeitura;
        var comPiso = pedida > opcoes.PisoValidadeUrlLeitura ? pedida : opcoes.PisoValidadeUrlLeitura;
        return comPiso < TetoValidade ? comPiso : TetoValidade;
    }

    /// <summary>
    /// A URL served from the cache must still cover the whole exhibition, so the entry lives at most validity − exhibition.
    /// When the cap leaves no such slack the exhibition cannot be covered by any URL; the entry then lives the
    /// margin, and the panel re-reads an expired URL.
    /// </summary>
    public static TimeSpan DuracaoCache(TimeSpan validade, TimeSpan duracaoTotal, OpcoesMidia opcoes)
    {
        ArgumentNullException.ThrowIfNull(opcoes);
        var folga = validade - duracaoTotal;
        return folga > TimeSpan.Zero ? folga : opcoes.MargemLeitura;
    }

    private Task<bool> EnvioConfirmadoAsync(Midia midia, CancellationToken ct) =>
        db.EnviosMidia.AsNoTracking().AnyAsync(
            e => e.Id == midia.EnvioId && e.ChaveObjeto == midia.ChaveObjeto
                && e.ConfirmadoEm != null && e.RecusadoEm == null && e.RemovidoEm == null,
            ct);

    private async Task<UrlGerada> DoCacheOuAssinarAsync(Midia midia, TimeSpan validade, TimeSpan duracaoCache, CancellationToken ct)
    {
        var tenantId = tenantAccessor.MultiTenantContext?.TenantInfo?.Id ?? throw new UnauthorizedException();

        // The envio in the key makes a file swap miss without any removal; the validity keeps a short preview URL from
        // being served to a caller that needs hours.
        var chave = $"paf:{tenantId}:midia-url:{midia.UnidadeId:N}:{midia.Id:N}:{midia.EnvioId:N}:{(long)validade.TotalSeconds}";
        var entrada = new HybridCacheEntryOptions
        {
            Expiration = duracaoCache,
            LocalCacheExpiration = duracaoCache,
            Flags = HybridCacheEntryFlags.DisableCompression,
        };

        using var limite = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limite.CancelAfter(PrazoCache);
        try
        {
            // The factory signs under the request's token, not the cache deadline: a deadline hit must not cancel the
            // signature that the fallback below would then repeat.
            return await cache.GetOrCreateAsync(
                    chave,
                    (Servico: this, Midia: midia, Validade: validade, Pedido: ct),
                    static (estado, _) => new ValueTask<UrlGerada>(estado.Servico.AssinarAsync(estado.Midia, estado.Validade, estado.Pedido)),
                    entrada,
                    null,
                    limite.Token)
                .ConfigureAwait(false);
        }
        catch (Exception erro) when (erro is RedisException || (erro is OperationCanceledException && !ct.IsCancellationRequested))
        {
            LogSemCache(logger, midia.Id, erro.GetType().Name);
            return await AssinarAsync(midia, validade, ct).ConfigureAwait(false);
        }
    }

    private async Task<UrlGerada> AssinarAsync(Midia midia, TimeSpan validade, CancellationToken ct)
    {
        // SigV4 floors both the signing date and X-Amz-Expires to whole seconds, so the store's expiry is up to 2 s before
        // "now + validity": announce that, never a later instant.
        var expiraEm = relogio.GetUtcNow() + validade - ArredondamentoDaAssinatura;
        var url = await armazenamento.GerarUrlLeituraAsync(midia, validade, ct).ConfigureAwait(false);
        return new UrlGerada(url.IsAbsoluteUri ? url.AbsoluteUri : url.OriginalString, expiraEm);
    }

    /// <summary>The local provider answers a server-relative path, served by the API itself: resolve it against the API origin.</summary>
    private Uri Resolver(string url)
    {
        if (!url.StartsWith('/'))
        {
            return new Uri(url, UriKind.Absolute);
        }

        var origem = requisicao.Origin
            ?? throw new InvalidOperationException("URL relativa de mídia sem origem da API para resolver (OriginOptions:OriginUrl).");
        return new Uri(origem.TrimEnd('/') + url, UriKind.Absolute);
    }

    /// <summary>What the cache holds; never logged, the signature in the URL is a credential.</summary>
    public sealed record UrlGerada(string Url, DateTimeOffset ExpiraEm);

    [LoggerMessage(Level = LogLevel.Warning, Message = "PAF media read URL signed without the cache (cache unavailable): midia={MidiaId} falha={Falha}")]
    private static partial void LogSemCache(ILogger logger, Guid midiaId, string falha);
}
