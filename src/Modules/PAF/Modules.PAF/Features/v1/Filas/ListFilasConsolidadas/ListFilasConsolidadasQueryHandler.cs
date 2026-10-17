using FSH.Modules.PAF.Contracts.v1.Filas;
using FSH.Modules.PAF.Data;
using FSH.Modules.PAF.Escopo;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.PAF.Features.v1.Filas.ListFilasConsolidadas;

/// <summary>
/// The explicit aggregation path of ADR 0003: turns off ONLY the unit filter, by name, and narrows to the
/// caller's scope. The tenant filter stays on; the parameterless IgnoreQueryFilters() would drop it too.
/// </summary>
public sealed class ListFilasConsolidadasQueryHandler(PafDbContext db, EscopoUsuario escopo)
    : IQueryHandler<ListFilasConsolidadasQuery, IReadOnlyList<FilaDto>>
{
    private static readonly string[] SoOFiltroDeUnidade = [PafQueryFilters.Unidade];

    public async ValueTask<IReadOnlyList<FilaDto>> Handle(ListFilasConsolidadasQuery query, CancellationToken cancellationToken)
    {
        var unidades = await escopo.UnidadesAsync(cancellationToken).ConfigureAwait(false);

        return await db.Filas.IgnoreQueryFilters(SoOFiltroDeUnidade).AsNoTracking()
            .Where(f => unidades.Contains(f.UnidadeId))
            .OrderBy(f => f.UnidadeId)
            .ThenBy(f => f.Nome)
            .Select(f => new FilaDto(
                f.Id, f.UnidadeId, f.Nome, f.NomeExibicao, f.HoraInicial, f.HoraFinal, f.Ativa,
                db.FilaPrioridades.IgnoreQueryFilters(SoOFiltroDeUnidade)
                    .Where(fp => fp.FilaId == f.Id)
                    .OrderBy(fp => fp.Ordem)
                    .Select(fp => new FilaPrioridadeDto(fp.Id, fp.PrioridadeId, fp.Letra, fp.NumeroInicial, fp.Ordem))
                    .ToList()))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }
}
