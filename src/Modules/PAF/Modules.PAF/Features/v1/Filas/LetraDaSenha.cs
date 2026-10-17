using System.Net;
using FSH.Framework.Core.Exceptions;
using FSH.Modules.PAF.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace FSH.Modules.PAF.Features.v1.Filas;

/// <summary>
/// The senha letter is unique in the whole unidade, across filas (RF02, decided 03/10/2026): the painel
/// announces only the senha. The friendly check runs first; UX_FilaPrioridades_Letra is the guarantee
/// when two saves race past it.
/// </summary>
internal static class LetraDaSenha
{
    private const string IndiceLetra = "UX_FilaPrioridades_Letra";
    private const string IndicePar = "UX_FilaPrioridades_FilaPrioridade";

    // The painel reads the senha aloud, and "a1" and "A1" sound the same.
    public static string Normalizar(string letra) => letra.Trim().ToUpperInvariant();

    public static async Task ExigirLivresAsync(
        PafDbContext db,
        IReadOnlyCollection<string> letras,
        Guid? exceto,
        CancellationToken cancellationToken)
    {
        // Runs under the unit filter: the current unidade is the one being written.
        var ocupadas = await db.FilaPrioridades.AsNoTracking()
            .Where(fp => letras.Contains(fp.Letra) && (exceto == null || fp.Id != exceto.Value))
            .Join(db.Filas.AsNoTracking(), fp => fp.FilaId, f => f.Id, (fp, f) => new { fp.Letra, Fila = f.Nome })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (ocupadas.Count > 0)
        {
            throw new CustomException(
                "Letra de senha já usada nesta unidade.",
                ocupadas.Select(o => $"A letra {o.Letra} já é usada na fila {o.Fila}."),
                HttpStatusCode.Conflict);
        }
    }

    public static async Task SalvarAsync(PafDbContext db, CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } pg
            && pg.ConstraintName is IndiceLetra or IndicePar)
        {
            string mensagem = pg.ConstraintName == IndiceLetra
                ? "Letra de senha já usada nesta unidade."
                : "A prioridade já está na grade desta fila.";
            throw new CustomException(mensagem, ex, HttpStatusCode.Conflict);
        }
    }
}
