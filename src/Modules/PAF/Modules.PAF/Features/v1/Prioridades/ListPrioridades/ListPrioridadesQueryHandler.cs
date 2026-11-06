using FSH.Modules.PAF.Contracts.v1.Prioridades;
using FSH.Modules.PAF.Data;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.PAF.Features.v1.Prioridades.ListPrioridades;

public sealed class ListPrioridadesQueryHandler(PafDbContext db)
    : IQueryHandler<ListPrioridadesQuery, IReadOnlyList<PrioridadeDto>>
{
    public async ValueTask<IReadOnlyList<PrioridadeDto>> Handle(ListPrioridadesQuery query, CancellationToken cancellationToken) =>
        await db.Prioridades.AsNoTracking()
            .OrderByDescending(p => p.Valor)
            .ThenBy(p => p.Nome)
            .Select(p => new PrioridadeDto(p.Id, p.UnidadeId, p.Nome, p.Valor, p.Ativa))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
}
