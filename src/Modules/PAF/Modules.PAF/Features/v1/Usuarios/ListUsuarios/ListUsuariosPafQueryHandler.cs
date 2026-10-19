using FSH.Framework.Core.Exceptions;
using FSH.Modules.Identity.Contracts.Services;
using FSH.Modules.PAF.Contracts.v1.Usuarios;
using FSH.Modules.PAF.Data;
using FSH.Modules.PAF.Escopo;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.PAF.Features.v1.Usuarios.ListUsuarios;

/// <summary>
/// RF08: the screen lists for editing only the users whose whole scope fits in the caller's, decided by
/// <see cref="EscopoUsuario.ExigirAlcanceSobreAsync"/> (the single rule), so an escopo empresa user appears only to
/// the tenant admin. A user with no scope at all fits any scope, by the same subset rule.
/// </summary>
public sealed class ListUsuariosPafQueryHandler(
    PafDbContext db,
    EscopoUsuario escopo,
    IUserProfileService usuarios,
    PapeisDoUsuarioPaf papeis)
    : IQueryHandler<ListUsuariosPafQuery, IReadOnlyList<UsuarioPafDto>>
{
    public async ValueTask<IReadOnlyList<UsuarioPafDto>> Handle(ListUsuariosPafQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        // ponytail: three queries per user (scope, roles), fine for the tens of users of a client; a set-based read
        // of scopes and roles if a tenant reaches hundreds.
        var lista = new List<UsuarioPafDto>();
        foreach (var usuario in await usuarios.GetListAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!Guid.TryParse(usuario.Id, out var usuarioId) || !await AlcancaAsync(usuarioId, cancellationToken).ConfigureAwait(false))
            {
                continue;
            }

            var unidades = await db.UsuariosUnidade.AsNoTracking()
                .Where(u => u.UsuarioId == usuarioId)
                .OrderBy(u => u.Ordem)
                .Select(u => u.UnidadeId)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            var perfil = await db.PerfisUsuario.AsNoTracking()
                .FirstOrDefaultAsync(p => p.UsuarioId == usuarioId, cancellationToken)
                .ConfigureAwait(false);
            lista.Add(new UsuarioPafDto(
                usuarioId,
                usuario.FirstName ?? string.Empty,
                usuario.LastName ?? string.Empty,
                usuario.Email ?? string.Empty,
                usuario.IsActive,
                perfil?.Funcao,
                perfil?.Ramal,
                perfil?.EscopoEmpresa ?? false,
                unidades,
                await papeis.DoUsuarioAsync(usuarioId, cancellationToken).ConfigureAwait(false)));
        }

        return [.. lista.OrderBy(u => u.Nome, StringComparer.CurrentCulture).ThenBy(u => u.Sobrenome, StringComparer.CurrentCulture)];
    }

    private async Task<bool> AlcancaAsync(Guid usuarioId, CancellationToken ct)
    {
        try
        {
            await escopo.ExigirAlcanceSobreAsync(usuarioId, ct).ConfigureAwait(false);
            return true;
        }
        catch (ForbiddenException)
        {
            // Out of reach is the expected answer for most users of a filial admin: it filters, it is not an error.
            return false;
        }
    }
}
