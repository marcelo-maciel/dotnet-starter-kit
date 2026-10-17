using FSH.Framework.Shared.Constants;

namespace FSH.Modules.PAF.Contracts.Authorization;

/// <summary>Permissões da sessão de guichê. Troca de unidade e logout exigem só autenticação.</summary>
public static class PafSessaoPermissions
{
    public static class Guiches
    {
        public const string Resource = "PAF.Guiches";
        public const string Selecionar = $"Permissions.{Resource}.Selecionar";
    }

    // IsBasic: o atendente comum opera guichê, e o perfil básico do kit é o ponto de partida dele.
    public static IReadOnlyList<FshPermission> All { get; } =
    [
        new("Selecionar guichê", "Selecionar", Guiches.Resource, IsBasic: true),
    ];
}
