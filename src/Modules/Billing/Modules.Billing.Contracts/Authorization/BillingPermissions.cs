using FSH.Framework.Shared.Constants;

namespace FSH.Modules.Billing.Contracts.Authorization;

public static class BillingPermissions
{
    public const string Resource = "Billing";
    public const string View   = $"Permissions.{Resource}.View";
    public const string Manage = $"Permissions.{Resource}.Manage";

    // Root only: billing is not part of the PAF product (31/10/2026), so a client's roles never reach it.
    public static IReadOnlyList<FshPermission> All { get; } =
    [
        new("View Billing",   ActionConstants.View, Resource, IsBasic: true, IsRoot: true),
        // Root only: the plan catalog it edits is global, so a tenant admin holding it changed every tenant's plans.
        new("Manage Billing", "Manage",             Resource, IsRoot: true),
    ];
}
