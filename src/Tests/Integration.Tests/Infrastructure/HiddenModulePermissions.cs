using Finbuckle.MultiTenant;
using Finbuckle.MultiTenant.Abstractions;
using FSH.Framework.Shared.Constants;
using FSH.Framework.Shared.Multitenancy;
using FSH.Modules.Identity.Contracts.Authorization;
using FSH.Modules.Identity.Data;
using FSH.Modules.Identity.Domain;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Integration.Tests.Infrastructure;

/// <summary>
/// The kit modules the PAF does not ship (Billing, Catalog, Chat, Files and Helpdesk) are root-only since 31/10/2026,
/// and so is the kit's Users, Roles and Groups administration since 02/11/2026, so a client tenant's Admin no longer
/// holds their permissions. Their tenant-isolation tests, and the tests that set up a client's users and roles through
/// the kit endpoints, still need a client tenant that uses them: this writes back on that tenant's Admin role the
/// permissions it held before the change (Billing.View only: Billing.Manage was root-only already), straight in the
/// table, the way a client seeded before the change kept them until the next startup sync. Call it before the admin
/// logs in, so no cached permission set predates the grant.
/// </summary>
internal static class HiddenModulePermissions
{
    /// <summary>What the kit's Users, Roles and Groups endpoints check; Users.Impersonate was root-only already.</summary>
    public static readonly string[] IdentityAdministration =
    [
        IdentityPermissions.Users.View,
        IdentityPermissions.Users.Create,
        IdentityPermissions.Users.Update,
        IdentityPermissions.Users.Delete,
        IdentityPermissions.Users.ManageRoles,
        IdentityPermissions.Users.ConfirmEmail,
        IdentityPermissions.Roles.View,
        IdentityPermissions.Roles.Create,
        IdentityPermissions.Roles.Update,
        IdentityPermissions.Roles.Delete,
        IdentityPermissions.Groups.View,
        IdentityPermissions.Groups.Create,
        IdentityPermissions.Groups.Update,
        IdentityPermissions.Groups.Delete,
        IdentityPermissions.Groups.ManageMembers,
    ];

    public static async Task GrantToTenantAdminAsync(WebApplicationFactory<Program> factory, string tenantId, IEnumerable<string> permissions)
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
