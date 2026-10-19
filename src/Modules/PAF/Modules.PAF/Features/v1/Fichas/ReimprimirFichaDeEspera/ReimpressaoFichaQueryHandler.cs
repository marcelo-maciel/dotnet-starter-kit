using FSH.Framework.Core.Exceptions;
using FSH.Modules.PAF.Contracts.v1.Fichas;
using FSH.Modules.PAF.Data;
using FSH.Modules.PAF.Escopo;
using FSH.Modules.PAF.Features.v1.Fichas.ConsultarFicha;
using FSH.Modules.PAF.Features.v1.Fichas.ObterImpressaoFicha;
using FSH.Modules.PAF.Parametros;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.PAF.Features.v1.Fichas.ReimprimirFichaDeEspera;

/// <summary>
/// RF20: finds the ficha of the current unit by printed senha, operational day and the fila printed on it (the
/// emission fila, not a forward's), then reads it through the single reprint reader of F06. Unit-filtered, so a
/// ficha or fila of another unit is the same 404 as an unknown one. Read only: nothing is emitted or stamped.
/// </summary>
public sealed class ReimpressaoFichaQueryHandler(PafDbContext db, IUnidadeCorrente unidadeCorrente, LeitorParametros parametros)
    : IQueryHandler<ReimpressaoFichaQuery, IReadOnlyList<FichaEmitidaDto>>
{
    public const string MensagemNaoEncontrada = "Não há ficha com essa senha nessa fila nesta unidade na data informada.";

    public async ValueTask<IReadOnlyList<FichaEmitidaDto>> Handle(ReimpressaoFichaQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        // No current unit = 403 before reading: the unit filter would answer an empty 404 that hides the cause.
        unidadeCorrente.Exigir();
        var senha = ConsultarFichaQueryHandler.Normalizar(query.Senha!);
        var data = query.Data!.Value;
        var filaId = query.FilaId!.Value;

        // Printed concatenation, as F14: a letter may hold digits, so "A12" can be A + 12 or A1 + 2 in the same fila.
        var fichaIds = await db.Fichas.AsNoTracking()
            .Where(f => f.DataOperacional == data && f.FilaId == filaId && f.Letra + f.Numero == senha)
            .OrderBy(f => f.CriadoEm).ThenBy(f => f.Id)
            .Select(f => f.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (fichaIds.Count == 0)
        {
            throw new NotFoundException(MensagemNaoEncontrada);
        }

        var fichas = new List<FichaEmitidaDto>(fichaIds.Count);
        foreach (var fichaId in fichaIds)
        {
            fichas.Add(await ImpressaoDaFicha.LerAsync(db, parametros, fichaId, cancellationToken).ConfigureAwait(false)
                ?? throw new NotFoundException(MensagemNaoEncontrada));
        }

        return fichas;
    }
}
