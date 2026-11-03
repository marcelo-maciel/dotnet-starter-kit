using FSH.Framework.Shared.Constants;

namespace FSH.Modules.Auditing.Contracts.Authorization;

public static class AuditingPermissions
{
    public static class AuditTrails
    {
        public const string Resource = nameof(AuditTrails);
        public const string View = $"Permissions.{Resource}.View";
        /// <summary>
        /// Allows querying audits across tenants by passing a TenantId filter.
        /// Without this permission, callers can only see their own tenant's audits.
        /// </summary>
        public const string ViewCrossTenant = $"Permissions.{Resource}.ViewCrossTenant";
    }

    public static IReadOnlyList<FshPermission> All { get; } =
    [
        // Not basic (S2, 03/11/2026): the trail shows every user's actions and IP in the tenant, so only the tenant's
        // Admin role holds it, not every user through Basic.
        new("View Audit Trails", ActionConstants.View, AuditTrails.Resource),
        new("View Audit Trails Across Tenants", "ViewCrossTenant", AuditTrails.Resource, IsRoot: true),
    ];
}
