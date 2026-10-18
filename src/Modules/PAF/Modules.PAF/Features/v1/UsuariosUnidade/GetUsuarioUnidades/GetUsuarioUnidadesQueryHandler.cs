using FSH.Modules.PAF.Contracts.v1.UsuariosUnidade;
using FSH.Modules.PAF.Data;
using FSH.Modules.PAF.Escopo;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.PAF.Features.v1.UsuariosUnidade.GetUsuarioUnidades;

/// <summary>
/// Who cannot see the whole scope of a user does not get to read nor edit it (RF08, arquitetura seção 5); an
/// escopo empresa user is read only by the tenant admin (<see cref="EscopoUsuario.AdministraTenantAsync"/>).
/// </summary>
public sealed class GetUsuarioUnidadesQueryHandler(PafDbContext db, EscopoUsuario escopo)
    : IQueryHandler<GetUsuarioUnidadesQuery, EscopoUsuarioDto>
{
    public async ValueTask<EscopoUsuarioDto> Handle(GetUsuarioUnidadesQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        await escopo.ExigirAlcanceSobreAsync(query.UsuarioId, cancellationToken).ConfigureAwait(false);

        var lista = await db.UsuariosUnidade.AsNoTracking()
            .Where(u => u.UsuarioId == query.UsuarioId)
            .OrderBy(u => u.Ordem)
            .Select(u => new UsuarioUnidadeDto(u.UnidadeId, u.Ordem))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        bool empresa = await escopo.EscopoEmpresaAsync(query.UsuarioId, cancellationToken).ConfigureAwait(false);
        return new EscopoUsuarioDto(empresa, lista);
    }
}
