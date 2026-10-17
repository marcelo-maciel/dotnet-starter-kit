using FSH.Modules.PAF.Contracts.v1.Estacoes;
using FSH.Modules.PAF.Data;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.PAF.Features.v1.Estacoes.ListEstacoes;

public sealed class ListEstacoesQueryHandler(PafDbContext db)
    : IQueryHandler<ListEstacoesQuery, IReadOnlyList<EstacaoDto>>
{
    public async ValueTask<IReadOnlyList<EstacaoDto>> Handle(ListEstacoesQuery query, CancellationToken cancellationToken)
    {
        var estacoes = await db.Estacoes.AsNoTracking()
            .OrderBy(e => e.Nome)
            .Select(e => new
            {
                e.Id,
                e.UnidadeId,
                e.Nome,
                e.Tipo,
                e.Ativa,
                Filas = db.EstacaoFilas.Where(ef => ef.EstacaoId == e.Id).OrderBy(ef => ef.Ordem)
                    .Select(ef => new { ef.Papel, ef.FilaId, ef.Ordem }).ToList(),
                Prioridades = db.EstacaoPrioridades.Where(ep => ep.EstacaoId == e.Id).OrderBy(ep => ep.Ordem)
                    .Select(ep => new EstacaoPrioridadeItem(ep.PrioridadeId, ep.Ordem)).ToList(),
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return estacoes
            .Select(e => new EstacaoDto(
                e.Id, e.UnidadeId, e.Nome, e.Tipo.ToString(), e.Ativa,
                e.Filas.Select(f => new EstacaoFilaItem(f.Papel.ToString(), f.FilaId, f.Ordem)).ToList(),
                e.Prioridades))
            .ToList();
    }
}
