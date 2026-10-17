using FSH.Modules.PAF.Contracts.v1.Unidades;
using FSH.Modules.PAF.Data;
using FSH.Modules.PAF.Escopo;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.PAF.Features.v1.Unidades.ListUnidades;

/// <summary>The unidades of the caller's scope, principal first: what the unit selector offers (ADR 0006).</summary>
public sealed class ListUnidadesQueryHandler(PafDbContext db, EscopoUsuario escopo)
    : IQueryHandler<ListUnidadesQuery, IReadOnlyList<UnidadeDto>>
{
    public async ValueTask<IReadOnlyList<UnidadeDto>> Handle(ListUnidadesQuery query, CancellationToken cancellationToken)
    {
        var usuarioId = escopo.ObterUsuarioId();
        return await db.UsuariosUnidade.AsNoTracking()
            .Where(uu => uu.UsuarioId == usuarioId)
            .Join(db.Unidades.AsNoTracking(), uu => uu.UnidadeId, u => u.Id, (uu, u) => new { uu.Ordem, Unidade = u })
            .OrderBy(x => x.Ordem)
            .Select(x => new UnidadeDto(x.Unidade.Id, x.Unidade.Nome, x.Unidade.DocumentoFiscal, x.Unidade.Ativa, x.Unidade.CriadoEm))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }
}
