using FSH.Modules.PAF.Contracts.v1.Contratos;
using FSH.Modules.PAF.Contracts.v1.Unidades;
using FSH.Modules.PAF.Data;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.PAF.Features.v1.Unidades.ListUnidadesOperador;

/// <summary>
/// Operator path of arquitetura seção 6: one client tenant per request (the kit's root tenant override), the tenant
/// filter stays, and only the unit filter goes off, by name, because the operator has no current unidade.
/// </summary>
public sealed class ListUnidadesOperadorQueryHandler(PafDbContext db)
    : IQueryHandler<ListUnidadesOperadorQuery, IReadOnlyList<UnidadeOperadorDto>>
{
    private static readonly string[] SoOFiltroDeUnidade = [PafQueryFilters.Unidade];

    public async ValueTask<IReadOnlyList<UnidadeOperadorDto>> Handle(ListUnidadesOperadorQuery query, CancellationToken cancellationToken)
    {
        var unidades = await db.Unidades.AsNoTracking()
            .OrderByDescending(u => u.Inicial)
            .ThenBy(u => u.CriadoEm)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var contratos = await db.ContratosUnidade.IgnoreQueryFilters(SoOFiltroDeUnidade).AsNoTracking()
            .OrderByDescending(c => c.InicioVigencia)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var porUnidade = contratos.ToLookup(c => c.UnidadeId);

        return [.. unidades.Select(u => new UnidadeOperadorDto(
            u.Id, u.Nome, u.DocumentoFiscal, u.Ativa, u.Inicial, u.CriadoEm,
            [.. porUnidade[u.Id].Select(c => new ContratoUnidadeDto(
                c.Id, c.UnidadeId, c.InicioVigencia, c.FimVigencia, c.Plano, c.LimiteSessoes, c.Politica.ToString()))]))];
    }
}
