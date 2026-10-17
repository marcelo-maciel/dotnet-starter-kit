using FSH.Framework.Core.Exceptions;
using FSH.Modules.PAF.Contracts.v1.UsuariosUnidade;
using FSH.Modules.PAF.Data;
using FSH.Modules.PAF.Escopo;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.PAF.Features.v1.UsuariosUnidade.GetUsuarioUnidades;

/// <summary>Who cannot see the whole list of a user does not get to read nor edit it (arquitetura seção 5).</summary>
public sealed class GetUsuarioUnidadesQueryHandler(PafDbContext db, EscopoUsuario escopo)
    : IQueryHandler<GetUsuarioUnidadesQuery, IReadOnlyList<UsuarioUnidadeDto>>
{
    public async ValueTask<IReadOnlyList<UsuarioUnidadeDto>> Handle(GetUsuarioUnidadesQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var doChamador = (await escopo.UnidadesAsync(cancellationToken).ConfigureAwait(false)).ToHashSet();
        var lista = await db.UsuariosUnidade.AsNoTracking()
            .Where(u => u.UsuarioId == query.UsuarioId)
            .OrderBy(u => u.Ordem)
            .Select(u => new UsuarioUnidadeDto(u.UnidadeId, u.Ordem))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (!lista.All(u => doChamador.Contains(u.UnidadeId)))
        {
            throw new ForbiddenException("Usuário com unidade fora do escopo de quem consulta.");
        }

        return lista;
    }
}
