using Finbuckle.MultiTenant;
using Finbuckle.MultiTenant.Abstractions;
using FSH.Framework.Shared.Constants;
using FSH.Framework.Shared.Multitenancy;
using FSH.Modules.Billing.Contracts.Authorization;
using FSH.Modules.Catalog.Contracts.Authorization;
using FSH.Modules.Chat.Contracts.Authorization;
using FSH.Modules.Files.Contracts.Authorization;
using FSH.Modules.Identity.Authorization;
using FSH.Modules.Identity.Contracts.Authorization;
using FSH.Modules.Identity.Contracts.Services;
using FSH.Modules.Identity.Data;
using FSH.Modules.Identity.Domain;
using FSH.Modules.Tickets.Contracts.Authorization;
using Integration.Tests.Infrastructure;

namespace Integration.Tests.Tests.Roles;

/// <summary>
/// Billing.Manage and Users.Impersonate are root-only: the plan catalog Billing.Manage edits is global, and acting
/// as a client's user is support done by the Codefi operator. So are the kit modules the PAF does not ship (Billing,
/// Catalog, Chat, Files and Helpdesk, 31/10/2026), except Files.Upload, which the profile photo needs, and the kit's
/// Users, Roles and Groups administration (02/11/2026), which /paf/usuarios replaces. A client's roles never hold
/// them, no role of the client can get them back, and a client seeded while they were still tenant permissions loses
/// them on the startup role sync. The client's Admin cannot read its roles through the API any more, so these tests
/// read them straight from the table, inside the tenant.
/// </summary>
[Collection(FshCollectionDefinition.Name)]
public sealed class RootOnlyTenantPermissionsTests
{
    private static readonly string[] HiddenModules =
    [
        .. new[] { BillingPermissions.All, CatalogPermissions.All, ChatPermissions.All, FilesPermissions.All, TicketsPermissions.All }
            .SelectMany(module => module)
            .Select(p => p.Name)
            .Where(name => name != FilesPermissions.Upload),
        .. HiddenModulePermissions.IdentityAdministration,
    ];

    private static readonly string[] RootOnly = [IdentityPermissions.Users.Impersonate, .. HiddenModules];

    // One list endpoint per hidden module, each gated by a permission the client no longer holds.
    private static readonly string[] HiddenModuleEndpoints =
    [
        "/api/v1/billing/invoices/me?pageNumber=1&pageSize=10",
        "/api/v1/billing/subscriptions/me",
        "/api/v1/catalog/brands?pageNumber=1&pageSize=10",
        "/api/v1/chat/channels?pageNumber=1&pageSize=10",
        "/api/v1/files/trash?pageNumber=1&pageSize=10",
        "/api/v1/tickets?pageNumber=1&pageSize=10",
        $"{TestConstants.IdentityBasePath}/users/search?PageNumber=1&PageSize=10",
        $"{TestConstants.IdentityBasePath}/roles?pageNumber=1&pageSize=10",
        $"{TestConstants.IdentityBasePath}/groups",
        $"{TestConstants.IdentityBasePath}/permissions/catalog",
    ];

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
        var (tenantAdmin, tenant) = await ProvisionTenantAsync(root, "admin");
        using var _t = tenantAdmin;
        var (_, tenantAdminPermissions) = await RoleAsync(tenant, RoleConstants.Admin);
        var (_, rootAdminPermissions) = await RoleAsync(await TenantAsync(TestConstants.RootTenantId), RoleConstants.Admin);

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
            targetTenantId = tenant.Id,
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
    public async Task TenantRoles_Should_NotGetThemBack_ThroughTheApiOrTheRoleService()
    {
        // The client's Admin no longer reaches the role endpoints at all. The role service under them is what the PAF
        // seed of the Administrador, Gerente and Atendente roles calls inside the tenant, and it drops the root-only
        // permissions of a request while keeping the allowed one.
        using var root = await _auth.CreateRootAdminClientAsync();
        var (tenantAdmin, tenant) = await ProvisionTenantAsync(root, "regrant");
        using var _t = tenantAdmin;
        var roleId = await CreateRoleAsync(tenant, $"Regrant-{Guid.NewGuid():N}"[..20]);

        string[] widened = [IdentityPermissions.Sessions.View, .. RootOnly];
        using (var create = await tenantAdmin.PostAsJsonAsync($"{TestConstants.IdentityBasePath}/roles",
            new { id = string.Empty, name = $"Api-{Guid.NewGuid():N}"[..20], description = "root-only permissions test role" }))
        {
            create.StatusCode.ShouldBe(HttpStatusCode.Forbidden, await create.Content.ReadAsStringAsync());
        }

        using (var put = await tenantAdmin.PutAsJsonAsync(
            $"{TestConstants.IdentityBasePath}/{roleId}/permissions", new { roleId, permissions = widened }))
        {
            put.StatusCode.ShouldBe(HttpStatusCode.Forbidden, await put.Content.ReadAsStringAsync());
        }

        (await PermissionsAsync(tenant, roleId)).ShouldBeEmpty("the forbidden request wrote nothing");

        await UpdatePermissionsThroughServiceAsync(tenant, roleId, widened);

        var stored = await PermissionsAsync(tenant, roleId);
        stored.ShouldBe([IdentityPermissions.Sessions.View], ignoreOrder: true,
            "the root-only permissions are dropped and the allowed one in the same request is kept");
    }

    [Fact]
    public async Task RoleSync_Should_TakeRootOnlyClaims_OffATenantSeededBefore()
    {
        // A client seeded while these were still tenant permissions keeps their claims; the sync on startup takes
        // them off every role of the client, Basic and a custom role included (its admin could hand them to any role
        // before), and the root tenant's are untouched.
        using var root = await _auth.CreateRootAdminClientAsync();
        var (tenantAdmin, tenant) = await ProvisionTenantAsync(root, "resync");
        using var _t = tenantAdmin;
        var customRoleId = await CreateRoleAsync(tenant, $"Resync-{Guid.NewGuid():N}"[..20]);
        var (adminRoleId, _) = await RoleAsync(tenant, RoleConstants.Admin);
        var (basicRoleId, _) = await RoleAsync(tenant, RoleConstants.Basic);

        using (var scope = EnterTenant(tenant))
        {
            var db = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
            db.RoleClaims.AddRange(new[] { adminRoleId, basicRoleId, customRoleId }.SelectMany(roleId => RootOnly, (roleId, p) => new FshRoleClaim
            {
                RoleId = roleId,
                ClaimType = ClaimConstants.Permission,
                ClaimValue = p,
                CreatedBy = "test",
                CreatedOn = DateTimeOffset.UtcNow,
            }));
            await db.SaveChangesAsync();
        }

        var adminBefore = await PermissionsAsync(tenant, adminRoleId);
        var customBefore = await PermissionsAsync(tenant, customRoleId);
        using (var scope = EnterTenant(tenant))
        {
            await scope.ServiceProvider.GetRequiredService<RolePermissionSyncer>().SyncAsync(CancellationToken.None);
        }

        var adminAfter = await PermissionsAsync(tenant, adminRoleId);
        var basicAfter = await PermissionsAsync(tenant, basicRoleId);
        var customAfter = await PermissionsAsync(tenant, customRoleId);
        var (_, rootAfter) = await RoleAsync(await TenantAsync(TestConstants.RootTenantId), RoleConstants.Admin);
        adminBefore.ShouldContain(BillingPermissions.Manage, "premise: the stale claim was written");
        adminBefore.ShouldContain(ChatPermissions.Channels.View, "premise: the stale claim was written");
        adminBefore.ShouldContain(IdentityPermissions.Roles.Update, "premise: the stale claim was written");
        customBefore.ShouldContain(IdentityPermissions.Users.Impersonate, "premise: the stale claim was written");
        adminAfter.Intersect(RootOnly).ShouldBeEmpty();
        basicAfter.Intersect(RootOnly).ShouldBeEmpty();
        adminAfter.ShouldNotBeEmpty("only the root-only claims go, the rest of the Admin role stays");
        basicAfter.ShouldContain(FilesPermissions.Upload, "the profile photo keeps its upload permission");
        customAfter.ShouldBeEmpty();
        RootOnly.ShouldBeSubsetOf(rootAfter);
    }

    [Fact]
    public async Task ProvisionedTenant_Should_HoldNoHiddenModulePermission_ButKeepFilesUpload()
    {
        // The seed of a new client (IdentityDbInitializer) and the startup sync give its Admin and Basic roles none of
        // the hidden modules' permissions, basic ones included; the root tenant's Admin keeps every one of them.
        using var root = await _auth.CreateRootAdminClientAsync();
        var (tenantAdmin, tenant) = await ProvisionTenantAsync(root, "seed");
        using var _t = tenantAdmin;
        var (adminRoleId, adminSeeded) = await RoleAsync(tenant, RoleConstants.Admin);
        var (basicRoleId, basicSeeded) = await RoleAsync(tenant, RoleConstants.Basic);
        using (var scope = EnterTenant(tenant))
        {
            await scope.ServiceProvider.GetRequiredService<RolePermissionSyncer>().SyncAsync(CancellationToken.None);
        }

        var adminSynced = await PermissionsAsync(tenant, adminRoleId);
        var basicSynced = await PermissionsAsync(tenant, basicRoleId);
        var (_, rootAdmin) = await RoleAsync(await TenantAsync(TestConstants.RootTenantId), RoleConstants.Admin);
        foreach (var permissions in new[] { adminSeeded, basicSeeded, adminSynced, basicSynced })
        {
            permissions.Intersect(HiddenModules).ShouldBeEmpty();
            permissions.ShouldContain(FilesPermissions.Upload, "the profile photo uploads through it");
        }

        HiddenModules.ShouldBeSubsetOf(rootAdmin);
    }

    [Fact]
    public async Task TenantAdmin_Should_Get403_OnEveryHiddenModule_WhileRootGets2xx()
    {
        using var root = await _auth.CreateRootAdminClientAsync();
        var (tenantAdmin, _) = await ProvisionTenantAsync(root, "hidden");
        using var _t = tenantAdmin;

        foreach (var endpoint in HiddenModuleEndpoints)
        {
            using var asTenant = await tenantAdmin.GetAsync(endpoint);
            using var asRoot = await root.GetAsync(endpoint);
            asTenant.StatusCode.ShouldBe(HttpStatusCode.Forbidden, $"{endpoint}: {await asTenant.Content.ReadAsStringAsync()}");
            asRoot.IsSuccessStatusCode.ShouldBeTrue($"{endpoint} as root: {(int)asRoot.StatusCode} {await asRoot.Content.ReadAsStringAsync()}");
        }

        // Files.Upload stays a client permission (profile photo), so the client's own file list still answers.
        using var mine = await tenantAdmin.GetAsync("/api/v1/files/mine?page=1&pageSize=10");
        mine.StatusCode.ShouldBe(HttpStatusCode.OK, await mine.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task TenantAdmin_Should_Get403_OnTheUsersRolesAndGroupsWrites()
    {
        // The writes only as the client: as root they would change the root tenant. The ids need not exist, the
        // permission check answers before the handler looks them up.
        using var root = await _auth.CreateRootAdminClientAsync();
        var (tenantAdmin, _) = await ProvisionTenantAsync(root, "writes");
        using var _t = tenantAdmin;
        var id = Guid.NewGuid();
        var unique = Guid.NewGuid().ToString("N")[..8];
        var writes = new (HttpMethod Method, string Path, object? Body)[]
        {
            (HttpMethod.Post, $"{TestConstants.IdentityBasePath}/register", new
            {
                firstName = "Cliente",
                lastName = "Kit",
                email = $"kit-{unique}@tenant.com",
                userName = $"kit{unique}",
                password = TestConstants.DefaultPassword,
                confirmPassword = TestConstants.DefaultPassword,
            }),
            (HttpMethod.Patch, $"{TestConstants.IdentityBasePath}/users/{id}", new { activateUser = false, userId = id }),
            (HttpMethod.Delete, $"{TestConstants.IdentityBasePath}/users/{id}", null),
            (HttpMethod.Post, $"{TestConstants.IdentityBasePath}/users/{id}/roles", new
            {
                userId = id,
                userRoles = new[] { new { roleName = RoleConstants.Admin, enabled = true } },
            }),
            (HttpMethod.Post, $"{TestConstants.IdentityBasePath}/users/{id}/confirm-email", null),
            (HttpMethod.Delete, $"{TestConstants.IdentityBasePath}/roles/{id}", null),
            (HttpMethod.Post, $"{TestConstants.IdentityBasePath}/groups", new { name = $"Grupo-{unique}", description = "kit" }),
            (HttpMethod.Delete, $"{TestConstants.IdentityBasePath}/groups/{id}", null),
            (HttpMethod.Post, $"{TestConstants.IdentityBasePath}/groups/{id}/members", new { userIds = new[] { id.ToString() } }),
        };

        foreach (var (method, path, body) in writes)
        {
            using var request = new HttpRequestMessage(method, path) { Content = body is null ? null : JsonContent.Create(body) };
            using var response = await tenantAdmin.SendAsync(request);
            response.StatusCode.ShouldBe(HttpStatusCode.Forbidden, $"{method} {path}: {await response.Content.ReadAsStringAsync()}");
        }
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

    private async Task<(HttpClient Client, AppTenantInfo Tenant)> ProvisionTenantAsync(HttpClient root, string label)
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
        return (await CreateTenantAdminClientWithRetryAsync(adminEmail, tenantId), await TenantAsync(tenantId));
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

    private async Task<string> CreateRoleAsync(AppTenantInfo tenant, string name)
    {
        using var scope = EnterTenant(tenant);
        var role = await scope.ServiceProvider.GetRequiredService<IRoleService>()
            .CreateOrUpdateRoleAsync(string.Empty, name, "root-only permissions test role", CancellationToken.None);
        return role.Id;
    }

    private async Task UpdatePermissionsThroughServiceAsync(AppTenantInfo tenant, string roleId, string[] permissions)
    {
        using var scope = EnterTenant(tenant);
        await scope.ServiceProvider.GetRequiredService<IRoleService>()
            .UpdatePermissionsAsync(roleId, [.. permissions], CancellationToken.None);
    }

    private async Task<(string Id, List<string> Permissions)> RoleAsync(AppTenantInfo tenant, string roleName)
    {
        string roleId;
        using (var scope = EnterTenant(tenant))
        {
            roleId = await scope.ServiceProvider.GetRequiredService<IdentityDbContext>().Roles
                .Where(r => r.Name == roleName)
                .Select(r => r.Id)
                .SingleAsync();
        }

        return (roleId, await PermissionsAsync(tenant, roleId));
    }

    private async Task<List<string>> PermissionsAsync(AppTenantInfo tenant, string roleId)
    {
        using var scope = EnterTenant(tenant);
        return await scope.ServiceProvider.GetRequiredService<IdentityDbContext>().RoleClaims
            .Where(rc => rc.RoleId == roleId && rc.ClaimType == ClaimConstants.Permission)
            .Select(rc => rc.ClaimValue!)
            .ToListAsync();
    }
}
