using FSH.Modules.PAF.Contracts.v1.Setores;
using FSH.Modules.PAF.Data;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.PAF.Features.v1.Setores.ListSetores;

/// <summary>Normal read: the unit filter keeps it to the current unidade, and to nothing without one.</summary>
public sealed class ListSetoresQueryHandler(PafDbContext db)
    : IQueryHandler<ListSetoresQuery, IReadOnlyList<SetorDto>>
{
    public async ValueTask<IReadOnlyList<SetorDto>> Handle(ListSetoresQuery query, CancellationToken cancellationToken) =>
        await db.Setores.AsNoTracking()
            .OrderBy(s => s.Nome)
            .Select(s => new SetorDto(s.Id, s.UnidadeId, s.Nome))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
}
