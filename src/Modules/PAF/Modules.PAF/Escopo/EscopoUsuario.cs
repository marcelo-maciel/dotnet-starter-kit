using FSH.Framework.Core.Context;
using FSH.Framework.Core.Exceptions;
using FSH.Modules.Identity.Contracts.Services;
using FSH.Modules.PAF.Contracts.Authorization;
using FSH.Modules.PAF.Data;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.PAF.Escopo;

/// <summary>
/// A user's effective unidade scope (RF08, ADR 0006): the single definition read by the middlewares, the unit
/// switch, the lists and the scope edits. "Unidades selecionadas" is the <c>UsuarioUnidade</c> list in its order;
/// "escopo empresa" is the principal row and then every unidade of the tenant by creation, including the ones
/// created after the user. Scope is also the boundary of what a caller may grant or edit, checked here on the
/// server and never trusted from the screen (arquitetura seção 5).
/// </summary>
public sealed class EscopoUsuario(PafDbContext db, ICurrentUser currentUser, IUserPermissionService permissoes)
{
    public const string MensagemForaDoEscopo = "Unidade fora do escopo do usuário.";
    public const string MensagemAlvoForaDoAlcance = "Usuário com unidade fora do escopo de quem consulta.";

    public Guid ObterUsuarioId()
    {
        var usuarioId = currentUser.GetUserId();
        return usuarioId == Guid.Empty ? throw new ForbiddenException("Usuário não identificado.") : usuarioId;
    }

    /// <summary>
    /// The tenant admin of RF08 reads and edits any user, grants any active unidade of the tenant and grants escopo
    /// empresa: the escopo empresa profile, or the holder of Unidades.Create (decision of 17/10/2026, option A), who
    /// keeps administering the tenant while no escopo empresa profile exists yet (RF23 seeds the first one).
    /// </summary>
    public async Task<bool> AdministraTenantAsync(CancellationToken cancellationToken)
    {
        var usuarioId = ObterUsuarioId();
        return await EscopoEmpresaAsync(usuarioId, cancellationToken).ConfigureAwait(false)
            || await permissoes.HasPermissionAsync(usuarioId.ToString(), PafPermissions.Unidades.Create, cancellationToken).ConfigureAwait(false);
    }

    public Task<bool> EscopoEmpresaAsync(Guid usuarioId, CancellationToken cancellationToken) =>
        db.PerfisUsuario.AsNoTracking().AnyAsync(p => p.UsuarioId == usuarioId && p.EscopoEmpresa, cancellationToken);

    /// <summary>Principal first. Inactive unidades stay in: scope says where the user may be, not where a login opens.</summary>
    public async Task<IReadOnlyList<Guid>> EscopoEfetivoAsync(Guid usuarioId, CancellationToken cancellationToken)
    {
        var lista = await db.UsuariosUnidade.AsNoTracking()
            .Where(u => u.UsuarioId == usuarioId)
            .OrderBy(u => u.Ordem)
            .Select(u => u.UnidadeId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        // An empresa profile without its principal row has no defined start: fail-closed, like an empty list.
        if (lista.Count == 0 || !await EscopoEmpresaAsync(usuarioId, cancellationToken).ConfigureAwait(false))
        {
            return lista;
        }

        var principal = lista[0];
        var demais = await db.Unidades.AsNoTracking()
            .Where(u => u.Id != principal)
            .OrderBy(u => u.CriadoEm)
            .ThenBy(u => u.Id)
            .Select(u => u.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return [principal, .. demais];
    }

    /// <summary>
    /// RF08: of <paramref name="unidades"/>, the ones the caller administers. The tenant admin administers all of them;
    /// anyone else only the unidades of his own effective scope.
    /// </summary>
    public async Task<IReadOnlySet<Guid>> AoAlcanceAsync(IEnumerable<Guid> unidades, CancellationToken cancellationToken)
    {
        var pedidas = unidades.ToHashSet();
        if (pedidas.Count == 0 || await AdministraTenantAsync(cancellationToken).ConfigureAwait(false))
        {
            return pedidas;
        }

        pedidas.IntersectWith(await EscopoEfetivoAsync(ObterUsuarioId(), cancellationToken).ConfigureAwait(false));
        return pedidas;
    }

    public async Task<bool> ContemAsync(Guid usuarioId, Guid unidadeId, CancellationToken cancellationToken) =>
        (await EscopoEfetivoAsync(usuarioId, cancellationToken).ConfigureAwait(false)).Contains(unidadeId);

    /// <summary>
    /// RF08: the caller only reads or edits a user whose whole effective scope fits in his own, so an escopo
    /// empresa user is reached only by the tenant admin, never by a list caller, even one holding every unidade.
    /// </summary>
    public async Task ExigirAlcanceSobreAsync(Guid usuarioAlvoId, CancellationToken cancellationToken)
    {
        if (await AdministraTenantAsync(cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        var chamadorId = ObterUsuarioId();

        if (await EscopoEmpresaAsync(usuarioAlvoId, cancellationToken).ConfigureAwait(false))
        {
            throw new ForbiddenException(MensagemAlvoForaDoAlcance);
        }

        var doChamador = (await EscopoEfetivoAsync(chamadorId, cancellationToken).ConfigureAwait(false)).ToHashSet();
        var doAlvo = await EscopoEfetivoAsync(usuarioAlvoId, cancellationToken).ConfigureAwait(false);
        if (!doAlvo.All(doChamador.Contains))
        {
            throw new ForbiddenException(MensagemAlvoForaDoAlcance);
        }
    }

    public async Task ExigirAsync(Guid unidadeId, CancellationToken cancellationToken)
    {
        if (!await ContemAsync(ObterUsuarioId(), unidadeId, cancellationToken).ConfigureAwait(false))
        {
            throw new ForbiddenException(MensagemForaDoEscopo);
        }
    }

    public const string MensagemSoAdministradorDaEmpresa = "Só o administrador da empresa cadastra, desativa e reativa unidades.";

    /// <summary>
    /// RF23: creating, disabling and enabling unidades belong to the escopo empresa profile alone. Unlike
    /// <see cref="AdministraTenantAsync"/>, holding Unidades.Create is not enough, or a filial admin holding it would
    /// open or close unidades outside his filial. A profile without its principal row has an empty scope (fail-closed).
    /// </summary>
    public async Task<bool> AdministraUnidadesAsync(CancellationToken cancellationToken)
    {
        var usuarioId = ObterUsuarioId();
        return await EscopoEmpresaAsync(usuarioId, cancellationToken).ConfigureAwait(false)
            && await db.UsuariosUnidade.AsNoTracking().AnyAsync(u => u.UsuarioId == usuarioId, cancellationToken).ConfigureAwait(false);
    }

    public async Task ExigirAdministracaoDeUnidadesAsync(CancellationToken cancellationToken)
    {
        if (!await AdministraUnidadesAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new ForbiddenException(MensagemSoAdministradorDaEmpresa);
        }
    }
}
