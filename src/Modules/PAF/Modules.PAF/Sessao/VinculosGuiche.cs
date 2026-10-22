using FSH.Framework.Core.Exceptions;
using FSH.Modules.PAF.Data;
using FSH.Modules.PAF.Domain;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.PAF.Sessao;

/// <summary>Regras do vínculo entre sessão e guichê (<see cref="SessaoEstacao"/>) usadas pela troca de unidade e de guichê.</summary>
internal static class VinculosGuiche
{
    public const string MensagemAtendimentoEmCurso =
        "Há um atendimento em curso no seu guichê. Encerre ou encaminhe antes de trocar.";

    public static Task<SessaoEstacao?> AtivoDaSessaoAsync(PafDbContext db, Guid sessaoId, CancellationToken ct) =>
        db.SessoesEstacao.AsNoTracking().FirstOrDefaultAsync(v => v.SessaoId == sessaoId && v.LiberadoEm == null, ct);

    public static Task<SessaoEstacao?> AtivoDaConcessaoAsync(PafDbContext db, Guid concessaoId, CancellationToken ct) =>
        db.SessoesEstacao.AsNoTracking().FirstOrDefaultAsync(v => v.ConcessaoId == concessaoId && v.LiberadoEm == null, ct);

    public static Task<SessaoEstacao?> AtivoDaEstacaoAsync(PafDbContext db, Guid estacaoId, CancellationToken ct) =>
        db.SessoesEstacao.AsNoTracking().FirstOrDefaultAsync(v => v.EstacaoId == estacaoId && v.LiberadoEm == null, ct);

    /// <summary>
    /// Trava o vínculo ativo da sessão até o fim da transação aberta e devolve a estação dele, ou nulo sem vínculo.
    /// A chamada e a soltura tomam esta trava primeiro, então nenhuma soltura cai entre a conferência "o guichê
    /// ainda é meu" da chamada e o commit dela (seção 8 item 1).
    /// </summary>
    public static async Task<Guid?> TravarAtivoDaSessaoAsync(PafDbContext db, string tenantId, Guid sessaoId, CancellationToken ct)
    {
        ExigirTransacao(db);
        var estacoes = await db.Database.SqlQuery<Guid>($"""
            SELECT "EstacaoId" AS "Value" FROM paf."SessoesEstacao"
             WHERE "TenantId" = {tenantId} AND "SessaoId" = {sessaoId} AND "LiberadoEm" IS NULL
               FOR UPDATE
            """).ToListAsync(ct);
        return estacoes.Count == 0 ? null : estacoes[0];
    }

    // ─── S11: dono do posto (sessão ou concessão de impersonação) ──────────────────

    /// <summary>Active link of the post's owner: the session's, or the impersonation grant's own (never the real user's).</summary>
    public static Task<SessaoEstacao?> AtivoDoDonoAsync(PafDbContext db, DonoPosto dono, CancellationToken ct) =>
        dono.Concessao is { } concessao
            ? AtivoDaConcessaoAsync(db, concessao.ConcessaoId, ct)
            : db.SessoesEstacao.AsNoTracking().FirstOrDefaultAsync(v => v.SessaoId == dono.SessaoId && v.LiberadoEm == null, ct);

    /// <summary>
    /// <see cref="TravarAtivoDaSessaoAsync"/> for the post's owner: under impersonation the grant's link is the one locked,
    /// so a release of the grant's guichê (unit switch, manager) waits for the support's call to commit, as the session's does.
    /// </summary>
    public static Task<Guid?> TravarAtivoDoDonoAsync(PafDbContext db, DonoPosto dono, CancellationToken ct) =>
        dono.Concessao is { } concessao
            ? TravarAtivoDaConcessaoAsync(db, dono.TenantId, concessao.ConcessaoId, ct)
            : TravarAtivoDaSessaoAsync(db, dono.TenantId, dono.SessaoId ?? throw new InvalidOperationException("Dono do posto sem sessão nem concessão."), ct);

    private static async Task<Guid?> TravarAtivoDaConcessaoAsync(PafDbContext db, string tenantId, Guid concessaoId, CancellationToken ct)
    {
        ExigirTransacao(db);
        var estacoes = await db.Database.SqlQuery<Guid>($"""
            SELECT "EstacaoId" AS "Value" FROM paf."SessoesEstacao"
             WHERE "TenantId" = {tenantId} AND "ConcessaoId" = {concessaoId} AND "LiberadoEm" IS NULL
               FOR UPDATE
            """).ToListAsync(ct);
        return estacoes.Count == 0 ? null : estacoes[0];
    }

    // ─── fim S11 ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Trava o vínculo ativo do guichê, seja qual for o dono, até o fim da transação aberta, e o devolve, ou nulo sem
    /// vínculo. A desativação da estação entra por aqui: uma chamada em voo no guichê ou já confirmou (e o atendimento
    /// é visto) ou espera e encontra o vínculo solto.
    /// </summary>
    public static async Task<VinculoTravado?> TravarAtivoDaEstacaoAsync(PafDbContext db, string tenantId, Guid estacaoId, CancellationToken ct)
    {
        ExigirTransacao(db);
        var vinculos = await db.Database.SqlQuery<VinculoTravado>($"""
            SELECT "Id", "SessaoId", "ConcessaoId" FROM paf."SessoesEstacao"
             WHERE "TenantId" = {tenantId} AND "EstacaoId" = {estacaoId} AND "LiberadoEm" IS NULL
               FOR UPDATE
            """).ToListAsync(ct);
        return vinculos.Count == 0 ? null : vinculos[0];
    }

    /// <summary>
    /// Solta o vínculo do guichê, recusando quando há atendimento chamado ou em atendimento nele. A linha do
    /// vínculo é travada antes da conferência: uma chamada em voo neste guichê ou já confirmou (e é vista) ou
    /// espera e encontra o vínculo solto. Verdadeiro só quando esta transação soltou o vínculo; falso quando outro
    /// escritor já o tinha soltado, e então a sessão pode estar em outro guichê, que não é desta liberação.
    /// </summary>
    public static async Task<bool> SoltarSemAtendimentoEmCursoAsync(PafDbContext db, string tenantId, SessaoEstacao vinculo, CancellationToken ct)
    {
        ExigirTransacao(db);
        var travados = await db.Database.SqlQuery<Guid>($"""
            SELECT "Id" AS "Value" FROM paf."SessoesEstacao"
             WHERE "TenantId" = {tenantId} AND "Id" = {vinculo.Id} AND "LiberadoEm" IS NULL
               FOR UPDATE
            """).ToListAsync(ct);
        if (travados.Count == 0)
        {
            // Released meanwhile by another writer (logout, manager): nothing left to release.
            return false;
        }

        // The station belongs to the unit being left, which may differ from the current one: only the unit filter is lifted.
        var emCurso = await db.Atendimentos.AsNoTracking()
            .IgnoreQueryFilters([PafQueryFilters.Unidade])
            .AnyAsync(a => a.EstacaoId == vinculo.EstacaoId
                && (a.Estado == EstadoAtendimento.Chamado || a.Estado == EstadoAtendimento.EmAtendimento), ct);
        if (emCurso)
        {
            throw new CustomException(MensagemAtendimentoEmCurso, [], System.Net.HttpStatusCode.Conflict);
        }

        await SoltarAsync(db, tenantId, vinculo.Id, ct);
        return true;
    }

    // Raw SQL skips the global filters: the tenant is explicit.
    public static Task<int> SoltarAsync(PafDbContext db, string tenantId, Guid vinculoId, CancellationToken ct) =>
        db.Database.ExecuteSqlAsync(
            $"""UPDATE paf."SessoesEstacao" SET "LiberadoEm" = now() WHERE "TenantId" = {tenantId} AND "Id" = {vinculoId} AND "LiberadoEm" IS NULL""",
            ct);

    // ─── S12: concessão de impersonação terminada ─────────────────────────────────

    /// <summary>
    /// Solta o vínculo ativo da concessão terminada (encerrada ou revogada no kit), com ou sem atendimento em curso no
    /// guichê: o atendimento fica no guichê para o RF12 (REQ l.599), então não há recusa, que no consumidor do evento
    /// viraria reentrega sem fim. Uma chamada do suporte em voo trava a linha (<see cref="TravarAtivoDoDonoAsync"/>), e
    /// este UPDATE espera o commit dela. Devolve quantos vínculos soltou: zero quando outro escritor já soltou (evento
    /// repetido, seleção de outro usuário, job de S08).
    /// </summary>
    public static Task<int> SoltarDaConcessaoAsync(PafDbContext db, string tenantId, Guid concessaoId, CancellationToken ct) =>
        db.Database.ExecuteSqlAsync($"""
            UPDATE paf."SessoesEstacao" SET "LiberadoEm" = now()
             WHERE "TenantId" = {tenantId} AND "ConcessaoId" = {concessaoId} AND "LiberadoEm" IS NULL
            """, ct);

    // ─── fim S12 ──────────────────────────────────────────────────────────────────

    // FOR UPDATE outside a transaction autocommits and holds nothing; fail loudly instead of locking nothing.
    private static void ExigirTransacao(PafDbContext db)
    {
        if (db.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("O travamento do vínculo do guichê exige transação aberta.");
        }
    }
}

/// <summary>Vínculo ativo travado: o dono é a sessão ou a concessão de impersonação, nunca as duas.</summary>
internal sealed record VinculoTravado(Guid Id, Guid? SessaoId, Guid? ConcessaoId);
