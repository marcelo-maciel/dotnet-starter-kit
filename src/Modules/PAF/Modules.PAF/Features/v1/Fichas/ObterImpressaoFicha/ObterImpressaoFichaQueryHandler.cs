using FSH.Framework.Core.Exceptions;
using FSH.Modules.PAF.Contracts.v1.Fichas;
using FSH.Modules.PAF.Data;
using FSH.Modules.PAF.Escopo;
using Mediator;

namespace FSH.Modules.PAF.Features.v1.Fichas.ObterImpressaoFicha;

public sealed class ObterImpressaoFichaQueryHandler(PafDbContext db, IUnidadeCorrente unidadeCorrente)
    : IQueryHandler<ObterImpressaoFichaQuery, FichaEmitidaDto>
{
    public const string MensagemFichaNaoEncontrada = "Ficha não encontrada.";

    public async ValueTask<FichaEmitidaDto> Handle(ObterImpressaoFichaQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        // No current unit = 403 before reading: the unit filter would answer an empty 404 that hides the cause.
        unidadeCorrente.Exigir();
        return await ImpressaoDaFicha.LerAsync(db, query.FichaId, cancellationToken).ConfigureAwait(false)
            ?? throw new NotFoundException(MensagemFichaNaoEncontrada);
    }
}
