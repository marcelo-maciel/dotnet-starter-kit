using System.Net;
using FSH.Framework.Core.Exceptions;
using FSH.Framework.Shared.Constants;
using FSH.Modules.Identity.Contracts.DTOs;
using FSH.Modules.Identity.Contracts.Services;
using FSH.Modules.PAF.Escopo;
using FSH.Modules.PAF.Provisionamento;

namespace FSH.Modules.PAF.Features.v1.Usuarios;

/// <summary>
/// The PAF product roles of a user (RF08: Administrador, Gerente, Atendente, seeded by <see cref="PapeisPadrao"/>). The
/// cadastro only ever touches these three, never a kit role, so no PAF screen can hand out the kit Admin. A role is
/// granted only by a caller holding every permission it carries, read from the tenant's role as it stands today.
/// </summary>
public sealed class PapeisDoUsuarioPaf(IRoleService papeis, IUserRoleService papeisDoUsuario, IUserPermissionService permissoes, EscopoUsuario escopo)
{
    public const string MensagemPapelInexistente = "O papel não existe neste cliente.";
    public const string MensagemPapelAcimaDoChamador = "O papel tem permissão que você não tem.";

    public static IReadOnlyList<string> Nomes { get; } = [PapeisPadrao.Administrador, PapeisPadrao.Gerente, PapeisPadrao.Atendente];

    public async Task ExigirConcedivelAsync(string papel, CancellationToken ct)
    {
        var existente = await PapeisPadrao.BuscarAsync(papeis, papel, ct).ConfigureAwait(false)
            ?? throw new CustomException(MensagemPapelInexistente, (IEnumerable<string>?)null, HttpStatusCode.Conflict);
        var doPapel = (await papeis.GetWithPermissionsAsync(existente.Id, ct).ConfigureAwait(false)).Permissions ?? [];
        var doChamador = (await permissoes.GetPermissionsAsync(escopo.ObterUsuarioId().ToString(), ct).ConfigureAwait(false) ?? [])
            .ToHashSet(StringComparer.Ordinal);
        if (!doPapel.All(doChamador.Contains))
        {
            throw new ForbiddenException(MensagemPapelAcimaDoChamador);
        }
    }

    public async Task<IReadOnlyList<string>> DoUsuarioAsync(Guid usuarioId, CancellationToken ct) =>
        (await ResumoDoUsuarioAsync(usuarioId, ct).ConfigureAwait(false)).Papeis;

    /// <summary>
    /// The PAF roles plus whether the user holds the kit Admin role, which the tenant's seeded administrator has instead of
    /// a PAF role: the screen shows that user as the client's administrator, not as a user with no profile.
    /// </summary>
    public async Task<(IReadOnlyList<string> Papeis, bool AdministradorDoCliente)> ResumoDoUsuarioAsync(Guid usuarioId, CancellationToken ct)
    {
        var todos = await papeisDoUsuario.GetUserRolesAsync(usuarioId.ToString(), ct).ConfigureAwait(false);
        bool Tem(string nome) => todos.Exists(r => r.Enabled && string.Equals(r.RoleName, nome, StringComparison.Ordinal));
        return ([.. Nomes.Where(Tem)], Tem(RoleConstants.Admin));
    }

    /// <summary>Leaves the user with exactly <paramref name="papeisDoPaf"/> among the three; his other roles stay.</summary>
    public Task DefinirAsync(Guid usuarioId, IReadOnlyCollection<string> papeisDoPaf, CancellationToken ct) =>
        papeisDoUsuario.AssignRolesAsync(
            usuarioId.ToString(),
            [.. Nomes.Select(nome => new UserRoleDto { RoleName = nome, Enabled = papeisDoPaf.Contains(nome) })],
            ct);
}
