using FSH.Framework.Core.Context;
using FSH.Framework.Core.Exceptions;
using FSH.Modules.PAF.Data;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.PAF.Escopo;

/// <summary>
/// The caller's ordered unidade scope (<c>UsuarioUnidade</c>). Scope is also the boundary of what the
/// caller may grant or edit, checked here on the server and never trusted from the screen (arquitetura seção 5).
/// </summary>
public sealed class EscopoUsuario(PafDbContext db, ICurrentUser currentUser)
{
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
