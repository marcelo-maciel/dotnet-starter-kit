using Finbuckle.MultiTenant;
using Finbuckle.MultiTenant.Abstractions;
using FSH.Framework.Shared.Constants;
using FSH.Framework.Shared.Multitenancy;
using FSH.Modules.Identity.Data;
using FSH.Modules.Identity.Domain;

namespace Integration.Tests.Infrastructure;

/// <summary>
/// The kit modules the PAF does not ship (Billing, Catalog, Chat, Files and Helpdesk) are root-only since 31/10/2026,
/// so a client tenant's Admin no longer holds their permissions. Their tenant-isolation tests still need a second
/// client tenant that uses the module: this writes back on that tenant's Admin role the permissions it held before the
/// change (Billing.View only: Billing.Manage was root-only already), straight in the table, the way a client seeded
/// before the change kept them until the next startup sync. Call it before the admin logs in, so no cached permission
/// set predates the grant.
/// </summary>
internal static class HiddenModulePermissions
{
    public static async Task GrantToTenantAdminAsync(FshWebApplicationFactory factory, string tenantId, IEnumerable<string> permissions)
    {
        using var scope = factory.Services.CreateScope();
        var tenant = await scope.ServiceProvider.GetRequiredService<IMultiTenantStore<AppTenantInfo>>().GetAsync(tenantId)
            ?? throw new InvalidOperationException($"Tenant '{tenantId}' não existe.");
        scope.ServiceProvider.GetRequiredService<IMultiTenantContextSetter>().MultiTenantContext =
            new MultiTenantContext<AppTenantInfo>(tenant);
        var db = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
        var adminRoleId = await db.Roles.Where(r => r.Name == RoleConstants.Admin).Select(r => r.Id).SingleAsync();
        var held = await db.RoleClaims
            .Where(rc => rc.RoleId == adminRoleId && rc.ClaimType == ClaimConstants.Permission)
            .Select(rc => rc.ClaimValue)
            .ToListAsync();
        db.RoleClaims.AddRange(permissions
            .Except(held)
            .Select(name => new FshRoleClaim
            {
                RoleId = adminRoleId,
                ClaimType = ClaimConstants.Permission,
                ClaimValue = name,
                CreatedBy = "test",
                CreatedOn = DateTimeOffset.UtcNow,
            }));
        await db.SaveChangesAsync();
    }
}
