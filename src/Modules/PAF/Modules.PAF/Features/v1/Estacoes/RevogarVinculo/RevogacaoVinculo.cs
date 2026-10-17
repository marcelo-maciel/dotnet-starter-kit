using FSH.Modules.PAF.Data;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.PAF.Features.v1.Estacoes;

internal static class RevogacaoVinculo
{
    // Npgsql translates DateTimeOffset.UtcNow inside the setter lambda to now(): the instant comes from the
    // PostgreSQL clock (seção 4.2), never from the app process.
    public static Task<int> RevogarVigentesAsync(PafDbContext db, Guid estacaoId, CancellationToken cancellationToken) =>
        db.VinculosEstacao
            .Where(v => v.EstacaoId == estacaoId && v.RevogadoEm == null)
            .ExecuteUpdateAsync(s => s.SetProperty(v => v.RevogadoEm, v => DateTimeOffset.UtcNow), cancellationToken);
}
