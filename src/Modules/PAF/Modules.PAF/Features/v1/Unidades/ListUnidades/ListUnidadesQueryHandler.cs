using FSH.Modules.PAF.Contracts.v1.Unidades;
using FSH.Modules.PAF.Data;
using FSH.Modules.PAF.Escopo;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.PAF.Features.v1.Unidades.ListUnidades;

/// <summary>The unidades of the caller's effective scope, principal first: what the unit selector offers (ADR 0006).</summary>
public sealed class ListUnidadesQueryHandler(PafDbContext db, EscopoUsuario escopo)
    : IQueryHandler<ListUnidadesQuery, IReadOnlyList<UnidadeDto>>
{
    public async ValueTask<IReadOnlyList<UnidadeDto>> Handle(ListUnidadesQuery query, CancellationToken cancellationToken)
    {
        var ordem = await escopo.EscopoEfetivoAsync(escopo.ObterUsuarioId(), cancellationToken).ConfigureAwait(false);
        var unidades = await db.Unidades.AsNoTracking()
            .Where(u => ordem.Contains(u.Id))
            .Select(u => new UnidadeDto(u.Id, u.Nome, u.DocumentoFiscal, u.Ativa, u.CriadoEm))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var posicao = ordem.Select((id, i) => (id, i)).ToDictionary(x => x.id, x => x.i);
        return [.. unidades.OrderBy(u => posicao[u.Id])];
    }
}
