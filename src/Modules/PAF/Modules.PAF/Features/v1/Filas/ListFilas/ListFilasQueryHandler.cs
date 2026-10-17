using FSH.Modules.PAF.Contracts.v1.Filas;
using FSH.Modules.PAF.Data;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.PAF.Features.v1.Filas.ListFilas;

/// <summary>Normal read: the unit filter keeps it to the current unidade, and to nothing without one.</summary>
public sealed class ListFilasQueryHandler(PafDbContext db)
    : IQueryHandler<ListFilasQuery, IReadOnlyList<FilaDto>>
{
    public async ValueTask<IReadOnlyList<FilaDto>> Handle(ListFilasQuery query, CancellationToken cancellationToken) =>
        await db.Filas.AsNoTracking()
            .OrderBy(f => f.Nome)
            .Select(f => new FilaDto(
                f.Id, f.UnidadeId, f.Nome, f.NomeExibicao, f.HoraInicial, f.HoraFinal, f.Ativa,
                db.FilaPrioridades
                    .Where(fp => fp.FilaId == f.Id)
                    .OrderBy(fp => fp.Ordem)
                    .Select(fp => new FilaPrioridadeDto(fp.Id, fp.PrioridadeId, fp.Letra, fp.NumeroInicial, fp.Ordem))
                    .ToList()))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
}
