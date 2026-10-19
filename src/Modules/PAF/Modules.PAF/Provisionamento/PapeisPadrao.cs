using FSH.Modules.Identity.Contracts.Services;
using FSH.Modules.PAF.Contracts.Authorization;

namespace FSH.Modules.PAF.Provisionamento;

/// <summary>
/// The three product roles of RF08 (Administrador, Gerente, Atendente) as kit roles holding PAF permissions only,
/// seeded per tenant through the kit's <see cref="IRoleService"/>. They are the starting point the user cadastro
/// assigns (C12, C15); the tenant's initial admin keeps the kit Admin role.
/// </summary>
public static class PapeisPadrao
{
    public const string Administrador = "Administrador";
    public const string Gerente = "Gerente";
    public const string Atendente = "Atendente";

    /// <summary>Atendimento block plus manual emission (RF08): exactly the PAF basic permissions.</summary>
    public static IReadOnlyList<string> PermissoesAtendente { get; } =
    [
        PafPermissions.Guiches.Selecionar,
        PafPermissions.Atendimentos.Operar,
        PafPermissions.Atendimentos.Encaminhar,
        PafPermissions.Atendimentos.SelecionarFicha,
        PafPermissions.Fichas.Emitir,
    ];

    /// <summary>
    /// Atendente plus Controle de Fila, releasing a stuck guichê (RF04), VIP, statistics and read-only cadastros.
    /// No cadastro write and no parameter write: those are Administração.
    /// </summary>
    public static IReadOnlyList<string> PermissoesGerente { get; } =
    [
        .. PermissoesAtendente,
        PafPermissions.Guiches.Liberar,
        PafPermissions.Atendimentos.Vip,
        PafPermissions.FichasDoDia.View,
        PafPermissions.FichasDoDia.Cancelar,
        PafPermissions.FichasDoDia.Reabilitar,
        PafPermissions.FichasEmAtendimento.View,
        PafPermissions.Estatisticas.View,
        PafPermissions.Unidades.View,
        PafPermissions.Filas.View,
        PafPermissions.Prioridades.View,
        PafPermissions.Estacoes.View,
        PafPermissions.Contratos.View,
        PafPermissions.Midias.View,
        PafPermissions.Programacoes.View,
        PafPermissions.Parametros.View,
    ];

    /// <summary>
    /// Every client-side PAF permission except the ones exclusive to the company admin (RF23: creating and editing
    /// unidades, the company-level parameter default). <c>Unidades.Create</c> also makes its holder a tenant admin in
    /// <c>EscopoUsuario.AdministraTenantAsync</c>, so a unit admin must not get it. No kit permission: users are
    /// managed through the PAF endpoints, which check scope (RF08), never through <c>Permissions.Users.Update</c>.
    /// </summary>
    public static IReadOnlyList<string> PermissoesAdministrador { get; } =
    [
        .. PermissoesGerente,
        PafPermissions.Filas.Create,
        PafPermissions.Filas.Update,
        PafPermissions.Prioridades.Create,
        PafPermissions.Prioridades.Update,
        PafPermissions.Estacoes.Create,
        PafPermissions.Estacoes.Update,
        PafPermissions.VinculosEstacao.Generate,
        PafPermissions.VinculosEstacao.Revoke,
        PafPermissions.UsuariosUnidade.View,
        PafPermissions.UsuariosUnidade.Update,
        PafPermissions.Midias.Create,
        PafPermissions.Midias.Update,
        PafPermissions.Programacoes.Create,
        PafPermissions.Programacoes.Update,
        PafPermissions.Programacoes.Delete,
        PafPermissions.Parametros.UpdateUnidade,
        PafPermissions.Usuarios.View,
        PafPermissions.Usuarios.Manage,
    ];

    // Estatisticas.ViewPorAtendente (D10: Gerente and Administrador) is still IsRoot in PafPermissions, and the kit
    // RoleService strips root permissions in a client tenant; it joins these sets when that flag flips.
    private static readonly (string Nome, string Descricao, IReadOnlyList<string> Permissoes)[] Papeis =
    [
        (Administrador, "PAF: administração da unidade ou da empresa, conforme o escopo", PermissoesAdministrador),
        (Gerente, "PAF: controle de fila e acompanhamento da unidade", PermissoesGerente),
        (Atendente, "PAF: atendimento no guichê", PermissoesAtendente),
    ];

    /// <summary>
    /// Creates each missing role with its set. An existing role keeps the permissions an admin gave it; only an
    /// existing role with no permission at all (a seed that failed after creating it) gets its set again.
    /// Runs under the tenant context of the caller.
    /// </summary>
    public static async Task SemearAsync(IRoleService papeis, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(papeis);
        foreach (var (nome, descricao, permissoes) in Papeis)
        {
            var existente = await BuscarAsync(papeis, nome, ct);
            if (existente is not null
                && (await papeis.GetWithPermissionsAsync(existente.Id, ct)).Permissions is { Count: > 0 })
            {
                continue;
            }

            var papel = existente ?? await papeis.CreateOrUpdateRoleAsync(string.Empty, nome, descricao, ct);
            await papeis.UpdatePermissionsAsync(papel.Id, [.. permissoes], ct);
        }
    }

    // The kit search is a case-insensitive "contains" over name and description; only the exact name counts.
    private static async Task<Identity.Contracts.DTOs.RoleDto?> BuscarAsync(IRoleService papeis, string nome, CancellationToken ct)
    {
        var pagina = await papeis.GetRolesAsync(1, 200, nome, ct);
        return pagina.Items.FirstOrDefault(r => string.Equals(r.Name, nome, StringComparison.Ordinal));
    }
}
