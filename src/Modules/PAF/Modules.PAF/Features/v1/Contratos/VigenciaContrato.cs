using System.Net;
using FSH.Framework.Core.Exceptions;
using FSH.Modules.PAF.Data;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.PAF.Features.v1.Contratos;

/// <summary>
/// One contract in force per unidade per day (RF22, RF23): the licence picks the latest start among the valid ones,
/// so an overlap would silently hide a contract. Create and update refuse an overlap under the unidade's lock.
/// </summary>
internal static class VigenciaContrato
{
    public const string MensagemSobreposta = "A vigência se sobrepõe à de outro contrato desta unidade.";

    private static readonly string[] SoOFiltroDeUnidade = [PafQueryFilters.Unidade];

    /// <summary>
    /// Serializes the contract writes of the unidade, so two overlapping writes never both pass the check below.
    /// ponytail: advisory lock in the handler; the database-level rule is an EXCLUDE constraint over a daterange,
    /// which needs the btree_gist extension (UnidadeId and TenantId equality) and a migration.
    /// </summary>
    public static Task TravarUnidadeAsync(PafDbContext db, string tenantId, Guid unidadeId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

        // A transaction-scoped advisory lock taken in autocommit is released at once and protects nothing.
        if (db.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("A trava de contrato exige transação aberta.");
        }

        var chave = $"paf-contrato:{tenantId}:{unidadeId:D}";
        return db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtextextended({chave}, 0))", ct);
    }

    /// <summary>
    /// Both ends are inclusive calendar days and a null end is open. The Codefi operator has no current unidade, so
    /// the read turns off the unit filter by name, else it would see no contract at all and let every overlap pass.
    /// </summary>
    public static async Task ExigirSemSobreposicaoAsync(
        PafDbContext db, Guid unidadeId, Guid? contratoIgnorado, DateOnly inicio, DateOnly? fim, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        var fimEfetivo = fim ?? DateOnly.MaxValue;
        bool sobreposta = await db.ContratosUnidade.IgnoreQueryFilters(SoOFiltroDeUnidade).AsNoTracking()
            .AnyAsync(c => c.UnidadeId == unidadeId
                    && c.Id != contratoIgnorado
                    && c.InicioVigencia <= fimEfetivo
                    && (c.FimVigencia == null || c.FimVigencia >= inicio),
                ct)
            .ConfigureAwait(false);
        if (sobreposta)
        {
            throw new CustomException(MensagemSobreposta, [], HttpStatusCode.Conflict);
        }
    }
}
