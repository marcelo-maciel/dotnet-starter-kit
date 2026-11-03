using Finbuckle.MultiTenant;
using Finbuckle.MultiTenant.Abstractions;
using FSH.Framework.Shared.Constants;
using FSH.Framework.Shared.Multitenancy;
using FSH.Modules.Auditing.Contracts.Authorization;
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
using Microsoft.AspNetCore.Identity;

namespace Integration.Tests.Tests.Roles;

/// <summary>
/// Billing.Manage and Users.Impersonate are root-only: the plan catalog Billing.Manage edits is global, and acting
/// as a client's user is support done by the Codefi operator. So are the kit modules the PAF does not ship (Billing,
/// Catalog, Chat, Files and Helpdesk, 31/10/2026), except Files.Upload, which the profile photo needs, and the kit's
/// Users, Roles and Groups administration (02/11/2026), which /paf/usuarios replaces. A client's roles never hold
/// them, no role of the client can get them back, and a client seeded while they were still tenant permissions loses
/// them on the startup role sync. AuditTrails.View stopped being basic (03/11/2026): the client's Admin keeps it and
/// the client's Basic role loses it on the same sync. The client's Admin cannot read its roles through the API any more, so these tests
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
    public async Task RoleSync_Should_TakeNoLongerBasicClaims_OffTheClientBasicRole()
    {
        // AuditTrails.View stopped being basic (S2, 03/11/2026): a client seeded before keeps it on Basic, and the
        // startup sync takes it off there, while the client's Admin keeps it. A custom role keeps it (it was granted,
        // not inherited), so does a claim that is no registered permission, and the root tenant's Basic is not touched.
        const string Unregistered = "Permissions.NaoRegistrada.View";
        using var root = await _auth.CreateRootAdminClientAsync();
        var (tenantAdmin, tenant) = await ProvisionTenantAsync(root, "basic");
        using var _t = tenantAdmin;
        var rootTenant = await TenantAsync(TestConstants.RootTenantId);
        var (basicRoleId, _) = await RoleAsync(tenant, RoleConstants.Basic);
        var (adminRoleId, _) = await RoleAsync(tenant, RoleConstants.Admin);
        var (rootBasicRoleId, _) = await RoleAsync(rootTenant, RoleConstants.Basic);
        var customRoleId = await CreateRoleAsync(tenant, $"Basic-{Guid.NewGuid():N}"[..20]);
        await AddClaimsAsync(tenant, [(basicRoleId, AuditingPermissions.AuditTrails.View), (basicRoleId, Unregistered), (customRoleId, AuditingPermissions.AuditTrails.View)]);
        await AddClaimsAsync(rootTenant, [(rootBasicRoleId, AuditingPermissions.AuditTrails.View)]);

        try
        {
            (await PermissionsAsync(tenant, basicRoleId)).ShouldContain(AuditingPermissions.AuditTrails.View, "premise: the stale claim was written");
            using (var scope = EnterTenant(tenant))
            {
                await scope.ServiceProvider.GetRequiredService<RolePermissionSyncer>().SyncAsync(CancellationToken.None);
            }

            using (var scope = EnterTenant(rootTenant))
            {
                await scope.ServiceProvider.GetRequiredService<RolePermissionSyncer>().SyncAsync(CancellationToken.None);
            }

            var basicAfter = await PermissionsAsync(tenant, basicRoleId);
            basicAfter.ShouldNotContain(AuditingPermissions.AuditTrails.View);
            basicAfter.ShouldContain(Unregistered, "a claim that is no registered permission is left alone");
            basicAfter.ShouldContain(IdentityPermissions.Sessions.View, "the basic permissions stay");
            basicAfter.ShouldContain(FilesPermissions.Upload, "the basic permissions stay");
            (await PermissionsAsync(tenant, adminRoleId)).ShouldContain(AuditingPermissions.AuditTrails.View, "the client's Admin keeps the trail");
            (await PermissionsAsync(tenant, customRoleId)).ShouldContain(AuditingPermissions.AuditTrails.View, "a granted claim is not the Basic role's");
            (await PermissionsAsync(rootTenant, rootBasicRoleId)).ShouldContain(AuditingPermissions.AuditTrails.View, "the root tenant's Basic is not touched");
        }
        finally
        {
            // The root tenant is shared by every test of the collection: its Basic goes back to the seed.
            await RemoveClaimAsync(rootTenant, rootBasicRoleId, AuditingPermissions.AuditTrails.View);
        }
    }

    [Fact]
    public async Task BasicUser_Should_Get403_OnTheAuditTrail_WhileTheAdminGets200()
    {
        // A client's user with only the Basic role (the Atendente's kit role) no longer reads the trail of everyone's
        // actions and IPs, while the client's Admin still does. Its own permission list is checked too, so a 403 for
        // another reason cannot pass for this one.
        using var root = await _auth.CreateRootAdminClientAsync();
        var (tenantAdmin, tenant) = await ProvisionTenantAsync(root, "trilha");
        using var _t = tenantAdmin;
        var unique = Guid.NewGuid().ToString("N")[..8];
        var email = $"trilha-{unique}@tenant.com";
        await RegisterConfirmedUserAsync(tenant, email, $"trilha{unique}");
        using var asBasic = await _auth.CreateAuthenticatedClientAsync(email, TestConstants.DefaultPassword, tenant.Id);

        var own = await asBasic.GetFromJsonAsync<string[]>($"{TestConstants.IdentityBasePath}/permissions");
        own.ShouldNotBeNull().ShouldContain(IdentityPermissions.Sessions.View, "premise: the user holds the Basic role");
        own.ShouldNotContain(AuditingPermissions.AuditTrails.View);
        foreach (var endpoint in new[] { $"{TestConstants.AuditsBasePath}?pageNumber=1&pageSize=10", $"{TestConstants.AuditsBasePath}/summary" })
        {
            using var basic = await asBasic.GetAsync(endpoint);
            using var admin = await tenantAdmin.GetAsync(endpoint);
            basic.StatusCode.ShouldBe(HttpStatusCode.Forbidden, $"{endpoint}: {await basic.Content.ReadAsStringAsync()}");
            admin.StatusCode.ShouldBe(HttpStatusCode.OK, $"{endpoint} as the client's admin: {await admin.Content.ReadAsStringAsync()}");
        }
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

    // Through the registration service inside the tenant: the client's register endpoint is root-only since 02/11/2026.
    // A registered user gets the Basic role and must confirm the email before the token endpoint lets it in.
    private async Task RegisterConfirmedUserAsync(AppTenantInfo tenant, string email, string userName)
    {
        using var scope = EnterTenant(tenant);
        var userId = await scope.ServiceProvider.GetRequiredService<IUserRegistrationService>().RegisterAsync(
            "Atendente", "Trilha", email, userName, TestConstants.DefaultPassword, TestConstants.DefaultPassword,
            string.Empty, "http://localhost", CancellationToken.None);
        var users = scope.ServiceProvider.GetRequiredService<UserManager<FshUser>>();
        var user = (await users.FindByIdAsync(userId)).ShouldNotBeNull();
        (await users.ConfirmEmailAsync(user, await users.GenerateEmailConfirmationTokenAsync(user))).Succeeded.ShouldBeTrue();
    }

    private async Task AddClaimsAsync(AppTenantInfo tenant, (string RoleId, string Permission)[] claims)
    {
        using var scope = EnterTenant(tenant);
        var db = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
        db.RoleClaims.AddRange(claims.Select(c => new FshRoleClaim
        {
            RoleId = c.RoleId,
            ClaimType = ClaimConstants.Permission,
            ClaimValue = c.Permission,
            CreatedBy = "test",
            CreatedOn = DateTimeOffset.UtcNow,
        }));
        await db.SaveChangesAsync();
    }

    private async Task RemoveClaimAsync(AppTenantInfo tenant, string roleId, string permission)
    {
        using var scope = EnterTenant(tenant);
        await scope.ServiceProvider.GetRequiredService<IdentityDbContext>().RoleClaims
            .Where(rc => rc.RoleId == roleId && rc.ClaimType == ClaimConstants.Permission && rc.ClaimValue == permission)
            .ExecuteDeleteAsync();
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
