using FSH.Framework.Shared.Constants;

namespace FSH.Modules.PAF.Contracts.Authorization;

public static class PafPermissions
{
    public static class VinculosEstacao
    {
        public const string Resource = "PafVinculosEstacao";
        public const string Generate = $"Permissions.{Resource}.Generate";
        public const string Revoke = $"Permissions.{Resource}.Revoke";
    }

    public static IReadOnlyList<FshPermission> All { get; } =
    [
        new("Generate PAF station link", "Generate", VinculosEstacao.Resource),
        new("Revoke PAF station link", "Revoke", VinculosEstacao.Resource),
    ];
}
