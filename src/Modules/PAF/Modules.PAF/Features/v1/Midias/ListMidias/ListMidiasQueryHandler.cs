using FSH.Modules.PAF.Contracts.v1.Midias;
using FSH.Modules.PAF.Data;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.PAF.Features.v1.Midias.ListMidias;

/// <summary>Normal read: the unit filter keeps it to the current unidade, and to nothing without one.</summary>
// ponytail: no paging, a unidade's catalog is tens of files; page it when a client passes a few hundred.
public sealed class ListMidiasQueryHandler(PafDbContext db) : IQueryHandler<ListMidiasQuery, IReadOnlyList<MidiaDto>>
{
    public async ValueTask<IReadOnlyList<MidiaDto>> Handle(ListMidiasQuery query, CancellationToken cancellationToken)
    {
        var midias = await db.Midias.AsNoTracking()
            .OrderBy(m => m.Descricao).ThenBy(m => m.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return midias.Select(MidiaTravada.ParaDto).ToList();
    }
}
