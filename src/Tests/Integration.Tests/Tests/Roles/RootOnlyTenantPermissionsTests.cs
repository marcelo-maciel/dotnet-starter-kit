using Finbuckle.MultiTenant;
using Finbuckle.MultiTenant.Abstractions;
using FSH.Framework.Shared.Constants;
using FSH.Framework.Shared.Multitenancy;
using FSH.Modules.Billing.Contracts.Authorization;
using FSH.Modules.Identity.Authorization;
using FSH.Modules.Identity.Contracts.Authorization;
using FSH.Modules.Identity.Data;
using FSH.Modules.Identity.Domain;
using Integration.Tests.Infrastructure;
using Integration.Tests.Infrastructure.Extensions;

namespace Integration.Tests.Tests.Roles;

/// <summary>
/// Billing.Manage and Users.Impersonate are root-only: the plan catalog Billing.Manage edits is global, and acting
/// as a client's user is support done by the Codefi operator. A client's Admin never holds them, cannot hand them
/// to a role, and a client seeded while they were still tenant permissions loses them on the startup role sync.
/// </summary>
[Collection(FshCollectionDefinition.Name)]
public sealed class RootOnlyTenantPermissionsTests
{
    private static readonly string[] RootOnly = [BillingPermissions.Manage, IdentityPermissions.Users.Impersonate];

    private readonly FshWebApplicationFactory _factory;
    private readonly AuthHelper _auth;

    public RootOnlyTenantPermissionsTests(FshWebApplicationFactory factory)
    {
        _factory = factory;
        _auth = new AuthHelper(factory);
    }

    [Fact]
    public async Task TenantAdmin_Should_HoldNeither_BillingManage_NorImpersonate()
    {
        using var root = await _auth.CreateRootAdminClientAsync();
        var (tenantAdmin, tenantId) = await ProvisionTenantAsync(root, "admin");
        using var _t = tenantAdmin;
        var (_, tenantAdminPermissions) = await RoleAsync(tenantAdmin, RoleConstants.Admin);
        var (_, rootAdminPermissions) = await RoleAsync(root, RoleConstants.Admin);

        using var plan = await tenantAdmin.PostAsJsonAsync("/api/v1/billing/plans", new
        {
            key = $"plan-{Guid.NewGuid():N}"[..20],
            name = "Plano do cliente",
            currency = "BRL",
            monthlyBasePrice = 1m,
        });
        using var impersonation = await tenantAdmin.PostAsJsonAsync($"{TestConstants.IdentityBasePath}/impersonation/start", new
        {
            targetUserId = Guid.NewGuid(),
            targetTenantId = tenantId,
            reason = "agir como um usuário do cliente",
        });

        tenantAdminPermissions.ShouldNotContain(BillingPermissions.Manage);
        tenantAdminPermissions.ShouldNotContain(IdentityPermissions.Users.Impersonate);
        rootAdminPermissions.ShouldContain(BillingPermissions.Manage, "the root operator keeps the plan catalog");
        rootAdminPermissions.ShouldContain(IdentityPermissions.Users.Impersonate, "the root operator keeps impersonation");
        plan.StatusCode.ShouldBe(HttpStatusCode.Forbidden, await plan.Content.ReadAsStringAsync());
        impersonation.StatusCode.ShouldBe(HttpStatusCode.Forbidden, await impersonation.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task TenantAdmin_Should_NotGiveThemBack_ThroughARole()
    {
        using var root = await _auth.CreateRootAdminClientAsync();
        var (tenantAdmin, _) = await ProvisionTenantAsync(root, "regrant");
        using var _t = tenantAdmin;
        var roleId = await CreateRoleAsync(tenantAdmin, $"Regrant-{Guid.NewGuid():N}"[..20]);

        string[] widened = [IdentityPermissions.Groups.View, .. RootOnly];
        using (var put = await tenantAdmin.PutAsJsonAsync(
            $"{TestConstants.IdentityBasePath}/{roleId}/permissions", new { roleId, permissions = widened }))
        {
            put.StatusCode.ShouldBe(HttpStatusCode.OK, await put.Content.ReadAsStringAsync());
        }

        var stored = await PermissionsAsync(tenantAdmin, roleId);
        stored.ShouldBe([IdentityPermissions.Groups.View], ignoreOrder: true,
            "the root-only permissions are dropped and the allowed one in the same request is kept");
    }

    [Fact]
    public async Task RoleSync_Should_TakeRootOnlyClaims_OffATenantSeededBefore()
    {
        // A client seeded while the two were still tenant permissions keeps their claims; the sync on startup takes
        // them off every role of the client, a custom role included (its admin could hand them to any role before),
        // and the root tenant's are untouched.
        using var root = await _auth.CreateRootAdminClientAsync();
        var (tenantAdmin, tenantId) = await ProvisionTenantAsync(root, "resync");
        using var _t = tenantAdmin;
        var customRoleId = await CreateRoleAsync(tenantAdmin, $"Resync-{Guid.NewGuid():N}"[..20]);
        var (adminRoleId, _) = await RoleAsync(tenantAdmin, RoleConstants.Admin);
        var tenant = await TenantAsync(tenantId);

        using (var scope = EnterTenant(tenant))
        {
            var db = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
            db.RoleClaims.AddRange(new[] { adminRoleId, customRoleId }.SelectMany(roleId => RootOnly, (roleId, p) => new FshRoleClaim
            {
                RoleId = roleId,
                ClaimType = ClaimConstants.Permission,
                ClaimValue = p,
                CreatedBy = "test",
                CreatedOn = DateTimeOffset.UtcNow,
            }));
            await db.SaveChangesAsync();
        }

        var adminBefore = await PermissionsAsync(tenantAdmin, adminRoleId);
        var customBefore = await PermissionsAsync(tenantAdmin, customRoleId);
        using (var scope = EnterTenant(tenant))
        {
            await scope.ServiceProvider.GetRequiredService<RolePermissionSyncer>().SyncAsync(CancellationToken.None);
        }

        var adminAfter = await PermissionsAsync(tenantAdmin, adminRoleId);
        var customAfter = await PermissionsAsync(tenantAdmin, customRoleId);
        var (_, rootAfter) = await RoleAsync(root, RoleConstants.Admin);
        adminBefore.ShouldContain(BillingPermissions.Manage, "premise: the stale claim was written");
        customBefore.ShouldContain(IdentityPermissions.Users.Impersonate, "premise: the stale claim was written");
        adminAfter.ShouldNotContain(BillingPermissions.Manage);
        adminAfter.ShouldNotContain(IdentityPermissions.Users.Impersonate);
        adminAfter.ShouldNotBeEmpty("only the root-only claims go, the rest of the Admin role stays");
        customAfter.ShouldBeEmpty();
        rootAfter.ShouldContain(BillingPermissions.Manage);
        rootAfter.ShouldContain(IdentityPermissions.Users.Impersonate);
    }

    // ─── helpers ─────────────────────────────────────────────────────

    private async Task<AppTenantInfo> TenantAsync(string tenantId)
    {
        using var scope = _factory.Services.CreateScope();
        var tenant = await scope.ServiceProvider.GetRequiredService<IMultiTenantStore<AppTenantInfo>>().GetAsync(tenantId);
        return tenant.ShouldNotBeNull();
    }

    // Synchronous on purpose: the tenant context is AsyncLocal, and a value set inside an async method does not flow
    // back to its caller, whose DbContext would then open without a tenant.
    private IServiceScope EnterTenant(AppTenantInfo tenant)
    {
        var scope = _factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<IMultiTenantContextSetter>().MultiTenantContext =
            new MultiTenantContext<AppTenantInfo>(tenant);
        return scope;
    }

    private async Task<(HttpClient Client, string TenantId)> ProvisionTenantAsync(HttpClient root, string label)
    {
        var unique = Guid.NewGuid().ToString("N")[..8];
        var tenantId = $"rootonly-{label}-{unique}";
        var adminEmail = $"rootonly-{label}-{unique}@tenant.com";
        using (var created = await root.PostAsJsonAsync(TestConstants.TenantsBasePath, new
        {
            id = tenantId,
            name = $"Tenant {tenantId}",
            connectionString = (string?)null,
            adminEmail,
            adminPassword = TestConstants.DefaultPassword,
            issuer = $"{tenantId}.issuer"
        }))
        {
            created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        }

        await TenantProvisioningWait.WaitForProvisioningAsync(root, tenantId);
        return (await CreateTenantAdminClientWithRetryAsync(adminEmail, tenantId), tenantId);
    }

    private async Task<HttpClient> CreateTenantAdminClientWithRetryAsync(string email, string tenant)
    {
        const int MaxRetries = 30;
        for (var i = 0; i < MaxRetries - 1; i++)
        {
            try
            {
                return await _auth.CreateAuthenticatedClientAsync(email, TestConstants.DefaultPassword, tenant);
            }
            catch (HttpRequestException)
            {
                await Task.Delay(1000);
            }
        }
        return await _auth.CreateAuthenticatedClientAsync(email, TestConstants.DefaultPassword, tenant);
    }

    private static async Task<string> CreateRoleAsync(HttpClient client, string name)
    {
        using var resp = await client.PostAsJsonAsync($"{TestConstants.IdentityBasePath}/roles",
            new { id = string.Empty, name, description = "root-only permissions test role" });
        resp.IsSuccessStatusCode.ShouldBeTrue($"Create role failed: {await resp.Content.ReadAsStringAsync()}");
        return (await resp.DeserializeAsync<RoleDto>()).Id;
    }

    private static async Task<(string Id, List<string> Permissions)> RoleAsync(HttpClient client, string roleName)
    {
        using var roles = await client.GetAsync($"{TestConstants.IdentityBasePath}/roles?pageSize=200");
        roles.StatusCode.ShouldBe(HttpStatusCode.OK);
        var role = (await roles.DeserializeAsync<PagedResponse<RoleDto>>()).Items.Single(r => r.Name == roleName);
        return (role.Id, await PermissionsAsync(client, role.Id));
    }

    private static async Task<List<string>> PermissionsAsync(HttpClient client, string roleId)
    {
        using var response = await client.GetAsync($"{TestConstants.IdentityBasePath}/{roleId}/permissions");
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.DeserializeAsync<RoleWithPermissions>()).Permissions ?? [];
    }

    private sealed record RoleWithPermissions(string Id, string Name, List<string>? Permissions);
}
