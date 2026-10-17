using FSH.Framework.Core.Context;
using FSH.Framework.Core.Exceptions;
using FSH.Modules.Identity.Contracts.Services;
using FSH.Modules.PAF.Contracts.Authorization;
using FSH.Modules.PAF.Data;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.PAF.Escopo;

/// <summary>
/// The caller's ordered unidade scope (<c>UsuarioUnidade</c>). Scope is also the boundary of what the
/// caller may grant or edit, checked here on the server and never trusted from the screen (arquitetura seção 5).
/// </summary>
public sealed class EscopoUsuario(PafDbContext db, ICurrentUser currentUser, IUserPermissionService permissoes)
{
    /// <summary>
    /// The tenant admin of RF08 reads, grants and edits the scope of any user with any active unidade of the
    /// tenant, outside the subset rule (decision of 17/10/2026).
    /// </summary>
    // ponytail: Unidades.Create stands for the tenant admin in the first slice; slice 2 replaces this criterion
    // with the RF08 "escopo empresa" (sees every unidade, including the ones created later).
    public Task<bool> AdministraTenantAsync(CancellationToken cancellationToken) =>
        permissoes.HasPermissionAsync(ObterUsuarioId().ToString(), PafPermissions.Unidades.Create, cancellationToken);

    public Guid ObterUsuarioId()
    {
        var usuarioId = currentUser.GetUserId();
        return usuarioId == Guid.Empty ? throw new ForbiddenException("Usuário não identificado.") : usuarioId;
    }

    public async Task<IReadOnlyList<Guid>> UnidadesAsync(CancellationToken cancellationToken)
    {
        var usuarioId = ObterUsuarioId();
        return await db.UsuariosUnidade.AsNoTracking()
            .Where(u => u.UsuarioId == usuarioId)
            .OrderBy(u => u.Ordem)
            .Select(u => u.UnidadeId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task ExigirAsync(Guid unidadeId, CancellationToken cancellationToken)
    {
        var usuarioId = ObterUsuarioId();
        bool contem = await db.UsuariosUnidade.AsNoTracking()
            .AnyAsync(u => u.UsuarioId == usuarioId && u.UnidadeId == unidadeId, cancellationToken)
            .ConfigureAwait(false);
        if (!contem)
        {
            throw new ForbiddenException("Unidade fora do escopo do usuário.");
        }
    }
}
