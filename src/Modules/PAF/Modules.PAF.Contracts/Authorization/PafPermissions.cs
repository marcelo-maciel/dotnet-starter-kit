using FSH.Framework.Shared.Constants;

namespace FSH.Modules.PAF.Contracts.Authorization;

/// <summary>
/// Person permissions of the PAF, named <c>Permissions.PAF.{Recurso}.{Acao}</c> (arquitetura seção 5).
/// Stations never pass through these: they use the station policy on their own claim.
/// </summary>
public static class PafPermissions
{
    public static class Unidades
    {
        public const string Resource = "PAF.Unidades";
        public const string View = $"Permissions.{Resource}.View";
        public const string Create = $"Permissions.{Resource}.Create";
        public const string Update = $"Permissions.{Resource}.Update";
    }

    public static class Filas
    {
        public const string Resource = "PAF.Filas";
        public const string View = $"Permissions.{Resource}.View";
        public const string Create = $"Permissions.{Resource}.Create";
        public const string Update = $"Permissions.{Resource}.Update";
    }

    public static class Prioridades
    {
        public const string Resource = "PAF.Prioridades";
        public const string View = $"Permissions.{Resource}.View";
        public const string Create = $"Permissions.{Resource}.Create";
        public const string Update = $"Permissions.{Resource}.Update";
    }

    public static class Estacoes
    {
        public const string Resource = "PAF.Estacoes";
        public const string View = $"Permissions.{Resource}.View";
        public const string Create = $"Permissions.{Resource}.Create";
        public const string Update = $"Permissions.{Resource}.Update";
    }

    public static class Contratos
    {
        public const string Resource = "PAF.Contratos";
        public const string View = $"Permissions.{Resource}.View";

        /// <summary>Root-only: plan and licence limit are written by Codefi, never by the client (RF23).</summary>
        public const string Manage = $"Permissions.{Resource}.Manage";
    }

    public static class UsuariosUnidade
    {
        public const string Resource = "PAF.UsuariosUnidade";
        public const string View = $"Permissions.{Resource}.View";
        public const string Update = $"Permissions.{Resource}.Update";
    }

    public static class VinculosEstacao
    {
        public const string Resource = "PAF.VinculosEstacao";
        public const string Generate = $"Permissions.{Resource}.Generate";
        public const string Revoke = $"Permissions.{Resource}.Revoke";
    }

    public static IReadOnlyList<FshPermission> All { get; } =
    [
        new("View PAF units", ActionConstants.View, Unidades.Resource),
        new("Create PAF units", ActionConstants.Create, Unidades.Resource),
        new("Update PAF units", ActionConstants.Update, Unidades.Resource),
        new("View PAF queues", ActionConstants.View, Filas.Resource),
        new("Create PAF queues", ActionConstants.Create, Filas.Resource),
        new("Update PAF queues", ActionConstants.Update, Filas.Resource),
        new("View PAF priorities", ActionConstants.View, Prioridades.Resource),
        new("Create PAF priorities", ActionConstants.Create, Prioridades.Resource),
        new("Update PAF priorities", ActionConstants.Update, Prioridades.Resource),
        new("View PAF stations", ActionConstants.View, Estacoes.Resource),
        new("Create PAF stations", ActionConstants.Create, Estacoes.Resource),
        new("Update PAF stations", ActionConstants.Update, Estacoes.Resource),
        new("View PAF unit contracts", ActionConstants.View, Contratos.Resource),
        new("Manage PAF unit contracts", "Manage", Contratos.Resource, IsRoot: true),
        new("View PAF user units", ActionConstants.View, UsuariosUnidade.Resource),
        new("Update PAF user units", ActionConstants.Update, UsuariosUnidade.Resource),
        new("Generate PAF station link", "Generate", VinculosEstacao.Resource),
        new("Revoke PAF station link", "Revoke", VinculosEstacao.Resource),
    ];
}
