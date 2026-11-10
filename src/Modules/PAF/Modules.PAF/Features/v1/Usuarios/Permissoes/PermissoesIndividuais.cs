using FSH.Framework.Core.Exceptions;
using FSH.Modules.Identity.Contracts.DTOs;
using FSH.Modules.Identity.Contracts.Services;
using FSH.Modules.PAF.Data;
using FSH.Modules.PAF.Escopo;
using FSH.Modules.PAF.Provisionamento;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.PAF.Features.v1.Usuarios.Permissoes;

/// <summary>
/// RF08 per-user permissions (C15) without touching the kit, which only knows roles: each user gets a personal kit role
/// holding his individual PAF permissions on top of his product role (<see cref="PapeisPadrao"/>) and the kit's basic
/// role. Permissions add up across roles, so a permission of the product role or a basic one cannot be taken from a
/// single user here; the product role is the starting point and the personal role only adds.
/// ponytail: one role per user shows up in the kit's roles screen; contribute per-user permissions to the kit
/// upstream if that becomes a nuisance.
/// </summary>
public sealed class PermissoesIndividuais(
    PafDbContext db,
    IRoleService papeis,
    IUserRoleService papeisDoUsuario,
    IUserPermissionService permissoes,
    EscopoUsuario escopo)
{
    public const string MensagemForaDoAlcanceDoChamador = "Você só concede ou retira permissão que você mesmo tem.";
    private const string DescricaoDoPapel = "PAF: permissões individuais de um usuário (não editar aqui)";

    /// <summary>Opaque, PII-free and deterministic, so two concurrent first saves look for the same role.</summary>
    public static string NomeDoPapel(Guid usuarioId) => $"PAF-usuario-{usuarioId:N}";

    public async Task<IReadOnlyList<string>> DoUsuarioAsync(Guid usuarioId, CancellationToken ct)
    {
        var papel = await PapeisPadrao.BuscarAsync(papeis, NomeDoPapel(usuarioId), ct).ConfigureAwait(false);
        return papel is null ? [] : await PermissoesDoPapelAsync(papel.Id, ct).ConfigureAwait(false);
    }

    /// <summary>What the user holds through every enabled role other than his personal one.</summary>
    public async Task<IReadOnlyList<string>> DosPapeisAsync(Guid usuarioId, CancellationToken ct)
    {
        var pessoal = NomeDoPapel(usuarioId);
        var resultado = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var papel in await papeisDoUsuario.GetUserRolesAsync(usuarioId.ToString(), ct).ConfigureAwait(false))
        {
            if (papel.Enabled && papel.RoleId is { } roleId && !string.Equals(papel.RoleName, pessoal, StringComparison.Ordinal))
            {
                resultado.UnionWith(await PermissoesDoPapelAsync(roleId, ct).ConfigureAwait(false));
            }
        }

        return [.. resultado];
    }

    public async Task<IReadOnlySet<string>> DoChamadorAsync(CancellationToken ct) =>
        (await permissoes.GetPermissionsAsync(escopo.ObterUsuarioId().ToString(), ct).ConfigureAwait(false) ?? [])
        .ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// RF08 "não atribui mais do que tem": every permission that would be added to or removed from the user's
    /// individual set must be one the caller holds; leaving untouched one he lacks is fine.
    /// </summary>
    public async Task ExigirAlteracaoPermitidaAsync(Guid usuarioId, IReadOnlyCollection<string> novas, CancellationToken ct)
    {
        var atuais = await DoUsuarioAsync(usuarioId, ct).ConfigureAwait(false);
        var alteradas = new HashSet<string>(atuais, StringComparer.Ordinal);
        alteradas.SymmetricExceptWith(novas);
        if (alteradas.Count == 0)
        {
            return;
        }

        var doChamador = await DoChamadorAsync(ct).ConfigureAwait(false);
        if (!alteradas.All(doChamador.Contains))
        {
            throw new ForbiddenException(MensagemForaDoAlcanceDoChamador);
        }
    }

    /// <summary>
    /// Leaves the user's personal role with exactly <paramref name="novas"/>, after
    /// <see cref="ExigirAlteracaoPermitidaAsync"/>, both under a per-user lock: two saves of the same user serialize
    /// (each checks the set the previous one wrote) and the first save creates the personal role only once (the kit
    /// ignores a failed role creation). The kit writes through its own context on the scope's shared connection and
    /// cannot join a PAF transaction, so the lock is a session advisory lock on that connection, held open meanwhile.
    /// </summary>
    public async Task DefinirSobTravaAsync(string tenantId, Guid usuarioId, IReadOnlyCollection<string> novas, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(novas);
        var chave = $"paf-permissoes:{tenantId}:{usuarioId}";
        await db.Database.OpenConnectionAsync(ct).ConfigureAwait(false);
        try
        {
            await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_lock(hashtextextended({chave}, 0))", ct).ConfigureAwait(false);
            try
            {
                await ExigirAlteracaoPermitidaAsync(usuarioId, novas, ct).ConfigureAwait(false);
                await DefinirAsync(usuarioId, novas, ct).ConfigureAwait(false);
            }
            finally
            {
                // Own deadline: the request may be the thing that was cancelled. A failed unlock is still released
                // when the pooled connection is reset (DISCARD ALL) on its return to the pool.
                using var prazo = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_unlock(hashtextextended({chave}, 0))", prazo.Token).ConfigureAwait(false);
            }
        }
        finally
        {
            await db.Database.CloseConnectionAsync().ConfigureAwait(false);
        }
    }

    private async Task DefinirAsync(Guid usuarioId, IReadOnlyCollection<string> novas, CancellationToken ct)
    {
        var nome = NomeDoPapel(usuarioId);
        var papel = await PapeisPadrao.BuscarAsync(papeis, nome, ct).ConfigureAwait(false);
        if (papel is null && novas.Count == 0)
        {
            return;
        }

        papel ??= await papeis.CreateOrUpdateRoleAsync(string.Empty, nome, DescricaoDoPapel, ct).ConfigureAwait(false);

        // Assign first: the permission update then invalidates this user's cached set as a holder of the role, and
        // the assignment invalidates it again, so the next request already sees the new set.
        await papeisDoUsuario.AssignRolesAsync(usuarioId.ToString(), [new UserRoleDto { RoleName = nome, Enabled = true }], ct)
            .ConfigureAwait(false);
        await papeis.UpdatePermissionsAsync(papel.Id, [.. novas], ct).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<string>> PermissoesDoPapelAsync(string roleId, CancellationToken ct) =>
        [.. (await papeis.GetWithPermissionsAsync(roleId, ct).ConfigureAwait(false)).Permissions ?? []];
}
