using FSH.Modules.PAF.Contracts.v1.Contratos;
using FSH.Modules.PAF.Data;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.PAF.Features.v1.Contratos.ListContratosUnidade;

public sealed class ListContratosUnidadeQueryHandler(PafDbContext db)
    : IQueryHandler<ListContratosUnidadeQuery, IReadOnlyList<ContratoUnidadeDto>>
{
    public async ValueTask<IReadOnlyList<ContratoUnidadeDto>> Handle(ListContratosUnidadeQuery query, CancellationToken cancellationToken)
    {
        var contratos = await db.ContratosUnidade.AsNoTracking()
            .OrderByDescending(c => c.InicioVigencia)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return contratos
            .Select(c => new ContratoUnidadeDto(
                c.Id, c.UnidadeId, c.InicioVigencia, c.FimVigencia, c.Plano, c.LimiteSessoes, c.Politica.ToString()))
            .ToList();
    }
}
