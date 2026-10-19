using Finbuckle.MultiTenant.Abstractions;
using FSH.Framework.Shared.Multitenancy;
using FSH.Modules.Identity.Contracts.Services;
using FSH.Modules.PAF.Data;
using FSH.Modules.PAF.Domain;
using FSH.Modules.PAF.Provisionamento;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FSH.Modules.PAF.Sessao;

/// <summary>
/// Job recorrente do Hangfire (ARQ seções 5 e 9), por tenant: grava a amostra de <see cref="ConsumoLicenca"/> de cada
/// unidade com contrato vigente, apaga as marcas de <see cref="AtividadeSessao"/> vencidas e fecha os vínculos de guichê
/// que ninguém mais solta: de sessão sem marca viva e de concessão encerrada, revogada ou vencida. É a rede de
/// segurança do guichê que ninguém tentou escolher; o atendimento em curso nele fica como está. Idempotente: duas
/// execuções na mesma janela gravam uma amostra só (<c>UX_ConsumosLicenca_Amostra</c>), e as solturas são
/// condicionais ao vínculo ainda ativo.
/// </summary>
public sealed class ManutencaoLicencas(IServiceScopeFactory escopos, ConjuntoLicencas conjunto, ILogger<ManutencaoLicencas> logger)
{
    public const string IdRecorrente = "paf-manutencao-licencas";

    // ponytail: fixed cron and sampling window (5 min, the first value the ARQ leaves open); move both to options
    // when the contract conferência or the measurement asks for another granularity. The cron fires on the window
    // boundaries, so a late run still lands in its own window.
    public const string Cron = "*/5 * * * *";
    public const int JanelaAmostraMinutos = 5;

    // One stalled tenant database must not hold the others; the whole tenant pass gets this deadline.
    private static readonly TimeSpan PrazoTenant = TimeSpan.FromSeconds(30);

    // Same order of magnitude as the hub filter's grant check: a cache or database answer, never a long wait.
    private static readonly TimeSpan PrazoConcessao = TimeSpan.FromSeconds(2);

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
                // A tenant whose database is down (a failed provisioning stays in the store) or past its deadline is
                // skipped; the next run picks it up, and every step is safe to repeat.
                logger.LogError(ex, "Manutenção de licenças pulou o tenant {TenantId}", tenant.Id);
            }
        }
    }

    /// <summary>Uma passada num tenant: amostra, marcas vencidas, vínculos de sessão sem licença e de concessão terminada.</summary>
    public async Task ExecutarNoTenantAsync(AppTenantInfo tenant, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        using var prazo = CancellationTokenSource.CreateLinkedTokenSource(ct);
        prazo.CancelAfter(PrazoTenant);
        await using var escopo = ProvisionamentoTenantPaf.AbrirEscopo(escopos, tenant);
        var db = escopo.ServiceProvider.GetRequiredService<PafDbContext>();

        await AmostrarAsync(db, tenant.Id, prazo.Token);
        await ApagarMarcasVencidasAsync(db, tenant.Id, prazo.Token);
        await SoltarVinculosSemLicencaAsync(db, tenant.Id, prazo.Token);
        await SoltarVinculosDeConcessaoTerminadaAsync(
            db, escopo.ServiceProvider.GetRequiredService<IImpersonationGrantService>(), tenant.Id, prazo.Token);
    }

    /// <summary>
    /// Uma amostra por unidade com contrato vigente, que é a que tem limite a conferir; a unidade sem contrato não tem
    /// saldo (RF22). A contagem é a do conjunto no Valkey, a que decide a ocupação; com ele fora ou não reconstruído,
    /// a das marcas vivas, como no modo degradado.
    /// </summary>
    private async Task AmostrarAsync(PafDbContext db, string tenantId, CancellationToken ct)
    {
        var contratos = await db.Database.SqlQuery<ContratoVigente>($"""
            SELECT DISTINCT ON ("UnidadeId") "UnidadeId", "LimiteSessoes"
              FROM paf."ContratosUnidade"
             WHERE "TenantId" = {tenantId}
               AND "InicioVigencia" <= (now() AT TIME ZONE 'America/Sao_Paulo')::date
               AND ("FimVigencia" IS NULL OR "FimVigencia" >= (now() AT TIME ZONE 'America/Sao_Paulo')::date)
             ORDER BY "UnidadeId", "InicioVigencia" DESC
            """).ToListAsync(ct);
        if (contratos.Count == 0)
        {
            return;
        }

        var unidades = contratos.Select(c => c.UnidadeId).ToArray();
        var ocupadas = await TamanhosAsync(db, tenantId, unidades, ct);
        foreach (var contrato in contratos)
        {
            // The window start, not the instant: a second run in the same window conflicts on the partial unique index.
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO paf."ConsumosLicenca" ("Id", "UnidadeId", "Tipo", "Ocupadas", "Limite", "SessaoId", "RegistradoEm", "TenantId")
                VALUES ({Guid.CreateVersion7()}, {contrato.UnidadeId}, {nameof(TipoConsumoLicenca.Amostra)},
                        {ocupadas.GetValueOrDefault(contrato.UnidadeId)}, {contrato.LimiteSessoes}, NULL,
                        date_bin(make_interval(mins => {JanelaAmostraMinutos}), now(), timestamptz '2000-01-01 00:00:00+00'),
                        {tenantId})
                ON CONFLICT DO NOTHING
                """, ct);
        }
    }

    private async Task<Dictionary<Guid, int>> TamanhosAsync(PafDbContext db, string tenantId, Guid[] unidades, CancellationToken ct)
    {
        if (await conjunto.TamanhosAsync(tenantId, unidades, ct) is { } tamanhos)
        {
            return unidades.Zip(tamanhos).ToDictionary(p => p.First, p => p.Second);
        }

        return await db.Database.SqlQuery<OcupadasDaUnidade>($"""
            SELECT "UnidadeId", count(*)::int AS "Ocupadas"
              FROM paf."AtividadesSessao"
             WHERE "TenantId" = {tenantId} AND "UnidadeId" = ANY({unidades})
               AND "UltimaAtividadeEm" > now() - make_interval(mins => {Licencas.PrazoMarcaMinutos})
             GROUP BY "UnidadeId"
            """).ToDictionaryAsync(o => o.UnidadeId, o => o.Ocupadas, ct);
    }

    // Every count already filters by the mark's deadline; the rows past it only take space (ARQ seção 9). The
    // boundary is the complement of the freshness test used everywhere else (> now() - prazo).
    private static Task<int> ApagarMarcasVencidasAsync(PafDbContext db, string tenantId, CancellationToken ct) =>
        db.Database.ExecuteSqlAsync($"""
            DELETE FROM paf."AtividadesSessao"
             WHERE "TenantId" = {tenantId}
               AND "UltimaAtividadeEm" <= now() - make_interval(mins => {Licencas.PrazoMarcaMinutos})
            """, ct);

    /// <summary>
    /// Vínculo de sessão sem marca viva: a sessão não ocupa licença, e o guichê seguiria bloqueado até alguém tentar
    /// escolhê-lo. A marca viva é o critério nos dois modos, porque é ela que a seleção do guichê confere e porque a
    /// renovação mantém marca e membro juntos. Só o vínculo é solto; o atendimento em curso no guichê fica.
    /// </summary>
    private static Task<int> SoltarVinculosSemLicencaAsync(PafDbContext db, string tenantId, CancellationToken ct) =>
        db.Database.ExecuteSqlAsync($"""
            UPDATE paf."SessoesEstacao" v SET "LiberadoEm" = now()
             WHERE v."TenantId" = {tenantId} AND v."LiberadoEm" IS NULL AND v."SessaoId" IS NOT NULL
               AND NOT EXISTS (SELECT 1 FROM paf."AtividadesSessao" a
                                WHERE a."TenantId" = {tenantId} AND a."SessaoId" = v."SessaoId"
                                  AND a."UltimaAtividadeEm" > now() - make_interval(mins => {Licencas.PrazoMarcaMinutos}))
            """, ct);

    /// <summary>
    /// Vínculo de concessão: vencida pelo valor guardado no vínculo (o vencimento não gera evento), e encerrada ou
    /// revogada pela conferência pública do kit, para o caso de o evento de concessão terminada se perder (ARQ seção 5).
    /// Concessão desconhecida conta como encerrada, como no kit.
    /// </summary>
    private static async Task SoltarVinculosDeConcessaoTerminadaAsync(
        PafDbContext db, IImpersonationGrantService concessoes, string tenantId, CancellationToken ct)
    {
        await db.Database.ExecuteSqlAsync($"""
            UPDATE paf."SessoesEstacao" SET "LiberadoEm" = now()
             WHERE "TenantId" = {tenantId} AND "LiberadoEm" IS NULL AND "ConcessaoId" IS NOT NULL
               AND "ConcessaoExpiraEm" <= now()
            """, ct);

        var ativos = await db.SessoesEstacao.AsNoTracking()
            .Where(v => v.ConcessaoId != null && v.LiberadoEm == null)
            .Select(v => new { v.Id, ConcessaoId = v.ConcessaoId!.Value })
            .ToListAsync(ct);
        foreach (var vinculo in ativos)
        {
            using var limite = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limite.CancelAfter(PrazoConcessao);
            if (await concessoes.IsRevokedOrEndedAsync(vinculo.ConcessaoId.ToString("N"), limite.Token))
            {
                await VinculosGuiche.SoltarAsync(db, tenantId, vinculo.Id, ct);
            }
        }
    }

    private sealed record ContratoVigente(Guid UnidadeId, int LimiteSessoes);

    private sealed record OcupadasDaUnidade(Guid UnidadeId, int Ocupadas);
}
