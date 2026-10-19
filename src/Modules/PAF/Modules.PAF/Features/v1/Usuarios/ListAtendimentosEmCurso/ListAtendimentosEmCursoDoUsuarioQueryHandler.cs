using FSH.Modules.PAF.Contracts.v1.Usuarios;
using FSH.Modules.PAF.Escopo;
using Mediator;

namespace FSH.Modules.PAF.Features.v1.Usuarios.ListAtendimentosEmCurso;

/// <summary>
/// What the deactivation and scope screens show before offering the release (RF08, RF22), only for a user who fits
/// in the caller's scope.
/// </summary>
public sealed class ListAtendimentosEmCursoDoUsuarioQueryHandler(EscopoUsuario escopo, AtendimentosEmCursoDoUsuario emCurso)
    : IQueryHandler<ListAtendimentosEmCursoDoUsuarioQuery, IReadOnlyList<AtendimentoEmCursoDoUsuarioDto>>
{
    public async ValueTask<IReadOnlyList<AtendimentoEmCursoDoUsuarioDto>> Handle(
        ListAtendimentosEmCursoDoUsuarioQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        await escopo.ExigirAlcanceSobreAsync(query.UsuarioId, cancellationToken).ConfigureAwait(false);
        return await emCurso.ListarAsync(query.UsuarioId, cancellationToken).ConfigureAwait(false);
    }
}
