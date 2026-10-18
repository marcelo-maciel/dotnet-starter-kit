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

    /// <summary>
    /// Solta o vínculo do guichê, recusando quando há atendimento chamado ou em atendimento nele. A linha do
    /// vínculo é travada antes da conferência: uma chamada em voo neste guichê ou já confirmou (e é vista) ou
    /// espera e encontra o vínculo solto.
    /// </summary>
    public static async Task SoltarSemAtendimentoEmCursoAsync(PafDbContext db, string tenantId, SessaoEstacao vinculo, CancellationToken ct)
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
            return;
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
    }

    // Raw SQL skips the global filters: the tenant is explicit.
    public static Task<int> SoltarAsync(PafDbContext db, string tenantId, Guid vinculoId, CancellationToken ct) =>
        db.Database.ExecuteSqlAsync(
            $"""UPDATE paf."SessoesEstacao" SET "LiberadoEm" = now() WHERE "TenantId" = {tenantId} AND "Id" = {vinculoId} AND "LiberadoEm" IS NULL""",
            ct);

    // FOR UPDATE outside a transaction autocommits and holds nothing; fail loudly instead of locking nothing.
    private static void ExigirTransacao(PafDbContext db)
    {
        if (db.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("O travamento do vínculo do guichê exige transação aberta.");
        }
    }
}
