using FSH.Modules.PAF.Contracts.Authorization;
using FSH.Modules.PAF.Data;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.PAF.Conferencia;

/// <summary>
/// The live check of a station credential (RF04), shared by the per-request middleware and the PafHub filter,
/// which covers the hub calls that never pass through the middleware.
/// </summary>
internal static class CredencialEstacao
{
    /// <summary>
    /// True while the link is not revoked, still holds a credential of <paramref name="papel"/> and the station
    /// is active. The caller sets the current unit first: <c>Estacoes</c> is unit-filtered and fails closed.
    /// </summary>
    public static Task<bool> VigenteAsync(PafDbContext db, Guid estacaoId, Guid vinculoId, string papel, CancellationToken ct)
    {
        var emissao = papel == PafPapeisEstacao.Emissao;
        return db.VinculosEstacao
            .AsNoTracking()
            .Where(v => v.Id == vinculoId && v.EstacaoId == estacaoId && v.RevogadoEm == null)
            .Where(v => emissao ? v.CredencialEmissaoHash != null : v.CredencialPainelHash != null)
            .AnyAsync(v => db.Estacoes.Any(e => e.Id == v.EstacaoId && e.Ativa), ct);
    }
}
