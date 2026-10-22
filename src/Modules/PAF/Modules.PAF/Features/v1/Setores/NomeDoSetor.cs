using System.Net;
using FSH.Framework.Core.Exceptions;
using FSH.Modules.PAF.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace FSH.Modules.PAF.Features.v1.Setores;

/// <summary>
/// The setor name is unique in the unidade (RF02): two homonyms would split one report line (RF07). The friendly check
/// runs first; UX_Setores_Nome is the guarantee when two saves race past it.
/// </summary>
internal static class NomeDoSetor
{
    public const int MaxLength = 120;
    private const string Indice = "UX_Setores_Nome";
    private const string MensagemRepetido = "Já existe um setor com este nome nesta unidade.";

    public static async Task ExigirLivreAsync(PafDbContext db, string nome, Guid? exceto, CancellationToken cancellationToken)
    {
        // Runs under the unit filter: the current unidade is the one being written.
        bool ocupado = await db.Setores.AsNoTracking()
            .AnyAsync(s => s.Nome == nome && (exceto == null || s.Id != exceto.Value), cancellationToken)
            .ConfigureAwait(false);
        if (ocupado)
        {
            throw new CustomException(MensagemRepetido, (IEnumerable<string>?)null, HttpStatusCode.Conflict);
        }
    }

    public static async Task SalvarAsync(PafDbContext db, CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: Indice })
        {
            throw new CustomException(MensagemRepetido, ex, HttpStatusCode.Conflict);
        }
    }
}
