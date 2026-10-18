using FSH.Modules.PAF.Contracts.v1.Filas;
using FSH.Modules.PAF.Data;
using FSH.Modules.PAF.Domain;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.PAF.Features.v1.Filas;

/// <summary>
/// Coverage of a (fila, prioridade) combination by the cadastro (RF02, decided 03/10/2026): covered while an
/// ACTIVE station lists the fila in its GUICHÊ role and has the prioridade enabled. The emission and painel
/// lists never count, and who is logged in does not matter. An uncovered combination leaves the emission
/// screens, because its fichas would never be called.
/// </summary>
/// <remarks>
/// Emission checks coverage under <see cref="TravarParaEmissaoAsync"/> (shared) and the station writes that
/// change coverage hold <see cref="TravarParaAlteracaoAsync"/> (exclusive), both until commit: an emission
/// that read the old coverage commits before the station write, so none passes after it.
/// </remarks>
internal static class CoberturaGuiche
{
    public const string MensagemSemCobertura = "Nenhum guichê atende esta fila nesta prioridade.";

    /// <summary>Covered pairs under the normal filters (tenant and current unidade).</summary>
    public static IQueryable<ParFilaPrioridade> Pares(PafDbContext db)
    {
        ArgumentNullException.ThrowIfNull(db);
        return Pares(db.Estacoes, db.EstacaoFilas, db.EstacaoPrioridades);
    }

    /// <summary>Covered pairs over the given sources, for the explicit cross-unit path (ADR 0003).</summary>
    public static IQueryable<ParFilaPrioridade> Pares(
        IQueryable<Estacao> estacoes,
        IQueryable<EstacaoFila> estacaoFilas,
        IQueryable<EstacaoPrioridade> estacaoPrioridades) =>
        from e in estacoes
        where e.Ativa
        join ef in estacaoFilas on e.Id equals ef.EstacaoId
        where ef.Papel == PapelEstacao.Guiche
        join ep in estacaoPrioridades on e.Id equals ep.EstacaoId
        select new ParFilaPrioridade { FilaId = ef.FilaId, PrioridadeId = ep.PrioridadeId };

    public static Task<bool> CobreAsync(PafDbContext db, Guid filaId, Guid prioridadeId, CancellationToken ct) =>
        Pares(db).AnyAsync(p => p.FilaId == filaId && p.PrioridadeId == prioridadeId, ct);

    /// <summary>
    /// Combinations of the current unidade that are covered now. Inside a transaction it sees the transaction's
    /// own flushed writes, which is how a station write compares before and after.
    /// </summary>
    public static async Task<IReadOnlyList<CombinacaoFilaPrioridadeDto>> CombinacoesCobertasAsync(PafDbContext db, CancellationToken ct)
    {
        var pares = Pares(db);
        return await (
                from fp in db.FilaPrioridades.AsNoTracking()
                join f in db.Filas.AsNoTracking() on fp.FilaId equals f.Id
                join p in db.Prioridades.AsNoTracking() on fp.PrioridadeId equals p.Id
                where pares.Any(c => c.FilaId == fp.FilaId && c.PrioridadeId == fp.PrioridadeId)
                orderby f.Nome, fp.Ordem
                select new CombinacaoFilaPrioridadeDto(fp.Id, f.Id, f.Nome, p.Id, p.Nome, fp.Letra))
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    public static Task TravarParaEmissaoAsync(PafDbContext db, string tenantId, Guid unidadeId, CancellationToken ct)
    {
        var chave = Chave(db, tenantId, unidadeId);
        return db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock_shared(hashtextextended({chave}, 0))", ct);
    }

    public static Task TravarParaAlteracaoAsync(PafDbContext db, string tenantId, Guid unidadeId, CancellationToken ct)
    {
        var chave = Chave(db, tenantId, unidadeId);
        return db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtextextended({chave}, 0))", ct);
    }

    // A transaction-scoped advisory lock taken in autocommit is released at once and protects nothing.
    private static string Chave(PafDbContext db, string tenantId, Guid unidadeId)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        if (db.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("A trava de cobertura exige transação aberta.");
        }

        return $"paf-cobertura:{tenantId}:{unidadeId:D}";
    }
}

/// <summary>Settable properties, not a positional record: EF Core translates member access only on a member-init projection.</summary>
internal sealed class ParFilaPrioridade
{
    public Guid FilaId { get; init; }
    public Guid PrioridadeId { get; init; }
}
