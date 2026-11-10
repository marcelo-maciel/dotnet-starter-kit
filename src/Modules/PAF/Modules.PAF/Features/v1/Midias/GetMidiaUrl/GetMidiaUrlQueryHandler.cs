using System.Net;
using FSH.Framework.Core.Exceptions;
using FSH.Modules.PAF.Contracts.v1.Midias;
using FSH.Modules.PAF.Data;
using FSH.Modules.PAF.Midias;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.PAF.Features.v1.Midias.GetMidiaUrl;

/// <summary>The unit filter is the unidade check: a mídia of another unidade or tenant is not found.</summary>
public sealed class GetMidiaUrlQueryHandler(PafDbContext db, UrlLeituraMidia leitura) : IQueryHandler<GetMidiaUrlQuery, UrlLeituraMidiaDto>
{
    public const string MensagemSemUrl = "Esta mídia está inativa ou o arquivo dela não foi confirmado, então não tem URL de leitura.";

    public async ValueTask<UrlLeituraMidiaDto> Handle(GetMidiaUrlQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var midia = await db.Midias.AsNoTracking()
            .FirstOrDefaultAsync(m => m.Id == query.Id, cancellationToken)
            .ConfigureAwait(false)
            ?? throw MidiaTravada.NaoEncontrada(query.Id);

        // A preview plays the mídia once.
        return await leitura.GerarAsync(midia, TimeSpan.FromSeconds(midia.DuracaoSegundos), cancellationToken).ConfigureAwait(false)
            ?? throw new CustomException(MensagemSemUrl, [], HttpStatusCode.Conflict);
    }
}
