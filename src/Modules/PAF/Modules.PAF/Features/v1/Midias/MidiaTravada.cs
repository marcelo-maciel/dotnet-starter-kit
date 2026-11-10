using System.Net;
using FSH.Framework.Core.Exceptions;
using FSH.Modules.PAF.Contracts.v1.Midias;
using FSH.Modules.PAF.Data;
using FSH.Modules.PAF.Domain;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.PAF.Features.v1.Midias;

/// <summary>Row lock and version check shared by every write on an existing mídia (RF03).</summary>
internal static class MidiaTravada
{
    public const string MensagemVersao = "Esta mídia foi alterada por outra pessoa. Recarregue e tente de novo.";

    /// <summary>
    /// <c>FOR UPDATE</c> on the mídia row, then the tracked read: two writes on the same mídia run one after the other,
    /// and the second one sees the first one's <c>Versao</c>. Needs the caller's open transaction.
    /// </summary>
    public static async Task<Midia> TravarAsync(PafDbContext db, string tenantId, Guid unidadeId, Guid midiaId, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("MidiaTravada.TravarAsync needs an open transaction.");
        }

        var travadas = await db.Database.SqlQuery<Guid>($"""
            SELECT "Id" AS "Value" FROM paf."Midias"
             WHERE "TenantId" = {tenantId} AND "UnidadeId" = {unidadeId} AND "Id" = {midiaId}
               FOR UPDATE
            """).ToListAsync(ct).ConfigureAwait(false);
        if (travadas.Count == 0)
        {
            throw NaoEncontrada(midiaId);
        }

        return await db.Midias.FirstOrDefaultAsync(m => m.Id == midiaId, ct).ConfigureAwait(false)
            ?? throw NaoEncontrada(midiaId);
    }

    public static void ExigirVersao(Midia midia, int versaoLida)
    {
        if (midia.Versao != versaoLida)
        {
            throw new CustomException(MensagemVersao, [], HttpStatusCode.Conflict);
        }
    }

    public static NotFoundException NaoEncontrada(Guid midiaId) => new($"Mídia {midiaId} não encontrada nesta unidade.");

    public static MidiaDto ParaDto(Midia m) =>
        new(m.Id, m.UnidadeId, m.Descricao, m.Tipo.ToString(), m.DuracaoSegundos, m.Ativa, m.Versao, m.CriadoEm);
}
