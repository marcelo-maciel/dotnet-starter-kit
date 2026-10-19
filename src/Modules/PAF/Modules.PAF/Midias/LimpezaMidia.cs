using Finbuckle.MultiTenant.Abstractions;
using FSH.Framework.Shared.Multitenancy;
using FSH.Framework.Shared.Storage;
using FSH.Framework.Storage.Services;
using FSH.Modules.PAF.Data;
using FSH.Modules.PAF.Domain;
using FSH.Modules.PAF.Provisionamento;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FSH.Modules.PAF.Midias;

/// <summary>What one tenant pass did. <c>SemResposta</c> = the pass stopped at a storage call past its deadline.</summary>
public sealed record ResultadoLimpezaMidia(int Removidos, int Falhas, int Indeterminados, bool SemResposta);

/// <summary>
/// Job recorrente do Hangfire (RF03, ADR 0007), por tenant: apaga do armazenamento o objeto dos envios que nenhuma
/// mídia referencia (substituídos, recusados ou nunca confirmados) passado o bloqueio de 30 dias do R2, e grava
/// <see cref="EnvioMidia.RemovidoEm"/> só depois de ver que o objeto sumiu. O remove do kit só registra a falha em log
/// (<c>S3StorageService.RemoveAsync</c>), e o HEAD do kit devolve null tanto para objeto ausente quanto para erro do
/// armazenamento; por isso o null só conta como "sumiu" quando o armazenamento já mostrou, nesta passada, que responde.
/// Idempotente: os carimbos são UPDATEs condicionais, e apagar uma chave ausente não é erro no S3.
/// </summary>
public sealed partial class LimpezaMidia(
    IServiceScopeFactory escopos,
    IOptions<OpcoesMidia> opcoes,
    ILogger<LimpezaMidia> logger)
{
    public const string IdRecorrente = "paf-limpeza-midia";

    // Six in the morning UTC is three in America/Sao_Paulo (no DST since 2019), outside store hours. ponytail: fixed cron
    // and batch; move both to the PAF:Midia options when the swaps of a tenant outgrow one batch a day.
    public const string Cron = "0 6 * * *";
    public const int Lote = 200;

    /// <summary>Same retention as <c>deploy/paf/r2/lock-midia.json</c> (maxAgeSeconds 2592000, Age rule on <c>paf-midia/</c>).</summary>
    public static readonly TimeSpan BloqueioArmazenamento = TimeSpan.FromDays(30);

    // A stalled tenant database or store must not hold the other tenants; each storage call also has its own deadline.
    private static readonly TimeSpan PrazoTenant = TimeSpan.FromMinutes(2);

    private readonly OpcoesMidia _opcoes = opcoes.Value;

    /// <summary>
    /// The lock counts from the PUT, which can land up to <see cref="OpcoesMidia.ValidadeUrlEnvio"/> after
    /// <c>CriadoEm</c>; deleting before that would be refused and retried for nothing.
    /// </summary>
    public TimeSpan Corte => BloqueioArmazenamento + _opcoes.ValidadeUrlEnvio;

    public async Task ExecutarAsync(CancellationToken ct)
    {
        IEnumerable<AppTenantInfo> tenants;
        await using (var escopo = escopos.CreateAsyncScope())
        {
            tenants = await escopo.ServiceProvider.GetRequiredService<IMultiTenantStore<AppTenantInfo>>().GetAllAsync();
        }

        foreach (var tenant in tenants.Where(t => !string.Equals(t.Id, MultitenancyConstants.Root.Id, StringComparison.Ordinal)))
        {
            try
            {
                await ExecutarNoTenantAsync(tenant, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                // Nothing is stamped before the store confirms, so the next run resumes where this one stopped.
                logger.LogError(ex, "Limpeza de mídia pulou o tenant {TenantId}", tenant.Id);
            }
        }
    }

    public async Task<ResultadoLimpezaMidia> ExecutarNoTenantAsync(AppTenantInfo tenant, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        using var prazo = CancellationTokenSource.CreateLinkedTokenSource(ct);
        prazo.CancelAfter(PrazoTenant);
        // The store comes from the tenant scope: with quotas on, the kit's decorator debits the tenant in context.
        await using var escopo = ProvisionamentoTenantPaf.AbrirEscopo(escopos, tenant);
        var resultado = await LimparAsync(
            escopo.ServiceProvider.GetRequiredService<PafDbContext>(),
            escopo.ServiceProvider.GetRequiredService<IStorageService>(),
            tenant.Id,
            prazo.Token);
        LogPassada(tenant.Id, resultado.Removidos, resultado.Falhas, resultado.Indeterminados, resultado.SemResposta);
        return resultado;
    }

    /// <summary>
    /// One batch of the tenant, oldest first. Raw SQL with the tenant in every table: the job has no current unit, and the
    /// unit filter would return no rows (fail-closed).
    /// </summary>
    public async Task<ResultadoLimpezaMidia> LimparAsync(PafDbContext db, IStorageService armazenamento, string tenantId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(armazenamento);
        var candidatos = await db.Database.SqlQuery<Candidato>($"""
            SELECT e."Id", e."ChaveObjeto", e."ConfirmadoEm", e."RecusadoEm"
              FROM paf."EnviosMidia" e
             WHERE e."TenantId" = {tenantId} AND e."RemovidoEm" IS NULL
               AND e."CriadoEm" < now() - {Corte}
               AND NOT EXISTS (SELECT 1 FROM paf."Midias" m WHERE m."TenantId" = {tenantId} AND m."EnvioId" = e."Id")
             ORDER BY e."CriadoEm", e."Id"
             LIMIT {Lote}
            """).ToListAsync(ct);

        var chamadas = new ChamadasArmazenamento(armazenamento, _opcoes.PrazoArmazenamento, ct);
        // A null HEAD proves absence only once the store has answered with an object in this pass.
        bool armazenamentoResponde = await SondarAsync(db, chamadas, tenantId, ct);
        int removidos = 0, falhas = 0, indeterminados = 0;
        foreach (var candidato in candidatos)
        {
            if (candidato.ConfirmadoEm is null && candidato.RecusadoEm is null && !await FecharEnvioAbertoAsync(db, tenantId, candidato.Id, ct))
            {
                continue;
            }

            var antes = await chamadas.HeadAsync(candidato.ChaveObjeto);
            if (antes.SemResposta)
            {
                return new ResultadoLimpezaMidia(removidos, falhas, indeterminados, SemResposta: true);
            }

            if (antes.Objeto is null)
            {
                if (!armazenamentoResponde)
                {
                    indeterminados++;
                    continue;
                }

                removidos += await CarimbarRemovidoAsync(db, tenantId, candidato.Id, ct);
                continue;
            }

            armazenamentoResponde = true;
            if (!await chamadas.RemoverAsync(candidato.ChaveObjeto))
            {
                return new ResultadoLimpezaMidia(removidos, falhas, indeterminados, SemResposta: true);
            }

            var depois = await chamadas.HeadAsync(candidato.ChaveObjeto);
            if (depois.SemResposta)
            {
                return new ResultadoLimpezaMidia(removidos, falhas, indeterminados, SemResposta: true);
            }

            if (depois.Objeto is not null)
            {
                // Still there: the bucket lock, a missing delete permission, or a store error the kit only logged.
                falhas++;
                LogRemocaoSemEfeito(candidato.Id);
                continue;
            }

            removidos += await CarimbarRemovidoAsync(db, tenantId, candidato.Id, ct);
        }

        return new ResultadoLimpezaMidia(removidos, falhas, indeterminados, SemResposta: false);
    }

    /// <summary>
    /// HEAD of one mídia's object, a key that should exist: an answer with the object proves the store is reachable.
    /// Missing (no mídia yet, or the object was removed by hand) leaves the pass in the conservative mode.
    /// </summary>
    private static async Task<bool> SondarAsync(PafDbContext db, ChamadasArmazenamento chamadas, string tenantId, CancellationToken ct)
    {
        var chave = await db.Database.SqlQuery<string>($"""
            SELECT "ChaveObjeto" AS "Value" FROM paf."Midias" WHERE "TenantId" = {tenantId} ORDER BY "CriadoEm" DESC LIMIT 1
            """).FirstOrDefaultAsync(ct);
        if (chave is null)
        {
            return false;
        }

        var sonda = await chamadas.HeadAsync(chave);
        return sonda.Objeto is not null;
    }

    /// <summary>
    /// A stale envio still open could be confirmed while its object is deleted. Closing it first (the same conditional
    /// stamp a refused confirmation writes) makes the confirmation lose its own conditional UPDATE instead.
    /// False = a confirmation won; the envio may be referenced now and is left alone.
    /// </summary>
    private static async Task<bool> FecharEnvioAbertoAsync(PafDbContext db, string tenantId, Guid envioId, CancellationToken ct) =>
        await db.Database.ExecuteSqlAsync($"""
            UPDATE paf."EnviosMidia" SET "RecusadoEm" = now()
             WHERE "TenantId" = {tenantId} AND "Id" = {envioId}
               AND "ConfirmadoEm" IS NULL AND "RecusadoEm" IS NULL AND "RemovidoEm" IS NULL
            """, ct) == 1;

    private static Task<int> CarimbarRemovidoAsync(PafDbContext db, string tenantId, Guid envioId, CancellationToken ct) =>
        db.Database.ExecuteSqlAsync($"""
            UPDATE paf."EnviosMidia" e SET "RemovidoEm" = now()
             WHERE e."TenantId" = {tenantId} AND e."Id" = {envioId} AND e."RemovidoEm" IS NULL
               AND NOT EXISTS (SELECT 1 FROM paf."Midias" m WHERE m."TenantId" = {tenantId} AND m."EnvioId" = e."Id")
            """, ct);

    /// <summary>
    /// Every call under its own deadline (RNF05). The kit's S3 provider catches every exception, the cancellation
    /// included, and answers null; so a null that arrives with the deadline fired is "no answer", never "gone".
    /// </summary>
    private sealed class ChamadasArmazenamento(IStorageService armazenamento, TimeSpan prazoChamada, CancellationToken ct)
    {
        public async Task<RespostaHead> HeadAsync(string chave)
        {
            using var limite = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limite.CancelAfter(prazoChamada);
            try
            {
                var objeto = await armazenamento.HeadObjectAsync(chave, limite.Token).WaitAsync(limite.Token);
                return limite.IsCancellationRequested ? RespostaHead.Expirada : new RespostaHead(objeto, SemResposta: false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return RespostaHead.Expirada;
            }
        }

        /// <summary>False = past the deadline. A failed delete is not reported by the kit; the HEAD after it tells.</summary>
        public async Task<bool> RemoverAsync(string chave)
        {
            using var limite = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limite.CancelAfter(prazoChamada);
            try
            {
                await armazenamento.RemoveAsync(chave, limite.Token).WaitAsync(limite.Token);
                return !limite.IsCancellationRequested;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return false;
            }
        }
    }

    private sealed record RespostaHead(StoredObjectMetadata? Objeto, bool SemResposta)
    {
        public static readonly RespostaHead Expirada = new(null, SemResposta: true);
    }

    private sealed record Candidato(Guid Id, string ChaveObjeto, DateTimeOffset? ConfirmadoEm, DateTimeOffset? RecusadoEm);

    // Envio ids only, never the key (it carries tenant and unit) nor a file name (RNF06).
    [LoggerMessage(Level = LogLevel.Warning, Message = "Envio de mídia {EnvioId}: remoção sem efeito, o objeto continua no armazenamento")]
    private partial void LogRemocaoSemEfeito(Guid envioId);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Limpeza de mídia no tenant {TenantId}: {Removidos} removidos, {Falhas} falhas, {Indeterminados} indeterminados, sem resposta {SemResposta}")]
    private partial void LogPassada(string tenantId, int removidos, int falhas, int indeterminados, bool semResposta);
}
