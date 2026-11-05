using System.Globalization;
using Finbuckle.MultiTenant;
using Finbuckle.MultiTenant.Abstractions;
using FSH.Framework.Shared.Constants;
using FSH.Framework.Shared.Identity.Claims;
using FSH.Framework.Shared.Multitenancy;
using FSH.Modules.Billing.Contracts;
using FSH.Modules.Billing.Data;
using FSH.Modules.Billing.Domain;
using FSH.Modules.Identity.Contracts.Authorization;
using FSH.Modules.Identity.Data;
using FSH.Modules.Identity.Domain;
using FSH.Modules.Multitenancy.Contracts;
using FSH.Modules.Multitenancy.Data;
using FSH.Modules.Multitenancy.Provisioning;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FSH.Starter.DbMigrator.DemoSeed;

/// <summary>
/// Owns the "rich demo content" that the dev environment needs to feel lived-in:
/// the <c>acme</c> and <c>globex</c> tenants, their subscriptions, demo users and
/// custom roles. Invoked by the migrator's
/// <c>seed-demo</c> verb — never by the API runtime.
///
/// Idempotent: every step checks before writing, so re-running the verb
/// against an already-seeded database is a no-op.
///
/// Naming: pre-2026-05-17 this lived in the API as <c>DevDataSeeder</c>
/// (a hosted service) — moved here so the API no longer mutates data on
/// startup, matching the same principle that pulled migrations out into
/// this project. See <c>docs/superpowers/specs/2026-05-14-remove-api-auto-migration-design.md</c>.
/// </summary>
internal sealed class DemoSeeder
{
    private readonly IServiceProvider _services;
    private readonly IConfiguration _config;
    private readonly ILogger<DemoSeeder> _logger;
    private string _sharedPassword = string.Empty;

    public static readonly DemoTenant Acme = new(
        Id: "acme",
        Name: "Acme Corp",
        AdminEmail: "admin@acme.com",
        Issuer: "fsh.demo.acme",
        PlanKey: "pro-annual");

    public static readonly DemoTenant Globex = new(
        Id: "globex",
        Name: "Globex",
        AdminEmail: "admin@globex.com",
        Issuer: "fsh.demo.globex",
        PlanKey: "free");

    public DemoSeeder(IServiceProvider services, IConfiguration config, ILogger<DemoSeeder> logger)
    {
        _services = services;
        _config = config;
        _logger = logger;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        // Sourced from configuration so the demo credential isn't hard-coded.
        _sharedPassword = _config["Seed:DemoPassword"]
            ?? throw new InvalidOperationException(
                "Seed:DemoPassword must be configured (see appsettings.Development.json).");

        await EnsureDemoTenantsExistAsync(cancellationToken).ConfigureAwait(false);
        await SeedRootSuperAdminAsync(cancellationToken).ConfigureAwait(false);

        foreach (var demo in new[] { Acme, Globex })
        {
            await SeedTenantSubscriptionAsync(demo, cancellationToken).ConfigureAwait(false);
            await SeedTenantUsersAsync(demo, cancellationToken).ConfigureAwait(false);
        }

        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation(
                "[demo-seed] complete · root superadmin + {Acme} + {Globex} populated with subscriptions / users",
                Acme.Id, Globex.Id);
        }
    }

    // ─── Tenant provisioning ────────────────────────────────────────────

    /// <summary>
    /// Adds the demo tenants to the catalog if missing, then walks them through
    /// the same <see cref="ITenantService"/> migrate + seed path the runtime
    /// uses. The provisioning service inside the migrator falls back to inline
    /// execution because Hangfire isn't running here — we get a synchronous
    /// "tenant is ready" before this method returns.
    /// </summary>
    private async Task EnsureDemoTenantsExistAsync(CancellationToken cancellationToken)
    {
        using var scope = _services.CreateScope();
        var tenantStore = scope.ServiceProvider.GetRequiredService<IMultiTenantStore<AppTenantInfo>>();
        var tenantService = scope.ServiceProvider.GetRequiredService<ITenantService>();
        var tenantDb = scope.ServiceProvider.GetRequiredService<TenantDbContext>();

        foreach (var demo in new[] { Acme, Globex })
        {
            var existing = await tenantStore.GetAsync(demo.Id).ConfigureAwait(false);
            if (existing is null)
            {
                if (_logger.IsEnabled(LogLevel.Information))
                {
                    _logger.LogInformation("[demo-seed] creating tenant '{TenantId}'", demo.Id);
                }
                var tenant = new AppTenantInfo(demo.Id, demo.Name, connectionString: string.Empty, demo.AdminEmail, demo.Issuer);
                tenant.SetValidity(DateTime.UtcNow.AddYears(1));
                await tenantDb.TenantInfo.AddAsync(tenant, cancellationToken).ConfigureAwait(false);
                await tenantDb.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                existing = tenant;
            }

            // Same per-tenant path the migrator's apply verb uses. The Identity initializer creates
            // the tenant admin.
            await tenantService.MigrateTenantAsync(existing, cancellationToken).ConfigureAwait(false);
            await tenantService.SeedTenantAsync(existing, cancellationToken).ConfigureAwait(false);

            await EnsureProvisioningRecordAsync(tenantDb, demo.Id, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Demo tenants are migrated + seeded inline above, bypassing the provisioning
    /// pipeline — so no <see cref="TenantProvisioning"/> row exists and the admin
    /// Provisioning panel would 404. Record a completed run (all steps done) so the
    /// panel shows a real "Completed" history instead. Idempotent: skips if a row
    /// already exists for the tenant.
    /// </summary>
    private static async Task EnsureProvisioningRecordAsync(TenantDbContext tenantDb, string tenantId, CancellationToken cancellationToken)
    {
        var alreadyTracked = await tenantDb.Set<TenantProvisioning>()
            .AnyAsync(p => p.TenantId == tenantId, cancellationToken)
            .ConfigureAwait(false);
        if (alreadyTracked)
        {
            return;
        }

        var provisioning = new TenantProvisioning(tenantId, Guid.NewGuid().ToString());
        foreach (var step in Enum.GetValues<TenantProvisioningStepName>())
        {
            var stepEntity = new TenantProvisioningStep(provisioning.Id, step);
            stepEntity.MarkRunning();
            stepEntity.MarkCompleted();
            provisioning.Steps.Add(stepEntity);
        }
        provisioning.MarkCompleted();

        tenantDb.Add(provisioning);
        await tenantDb.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    // ─── Subscription ──────────────────────────────────────────────────

    /// <summary>
    /// Attaches an active billing <see cref="Subscription"/> to the demo tenant so the dashboard's
    /// PLAN / subscription cards are populated out of the box. The real tenant-create path drives this
    /// via <c>TenantSubscribedIntegrationEvent</c>, but demo tenants are provisioned inline (see
    /// <see cref="EnsureDemoTenantsExistAsync"/>) and never publish it — so we write the row directly.
    ///
    /// Paid plans also get an issued term invoice, matching the real flow. It's written directly
    /// rather than via <c>IBillingService</c> so we don't publish <c>InvoiceIssuedIntegrationEvent</c>
    /// — the one-shot migrator has no outbox dispatcher and demo seeding shouldn't fire
    /// notifications/emails. The subscription's term is aligned to the tenant's <c>ValidUpto</c> so the
    /// dashboard's term matches the enforced validity window.
    ///
    /// Idempotent: skips when the tenant already has an active subscription.
    /// </summary>
    private async Task SeedTenantSubscriptionAsync(DemoTenant demo, CancellationToken cancellationToken)
    {
        using var scope = _services.CreateScope();
        var tenantStore = scope.ServiceProvider.GetRequiredService<IMultiTenantStore<AppTenantInfo>>();
        var tenant = await tenantStore.GetAsync(demo.Id).ConfigureAwait(false);
        if (tenant is null) return;

        // BillingDbContext is NOT tenant-filtered (TenantId is an explicit column), so no Finbuckle
        // context juggling is required — we scope by TenantId directly.
        var billingDb = scope.ServiceProvider.GetRequiredService<BillingDbContext>();

        var plan = await billingDb.Plans
            .FirstOrDefaultAsync(p => p.Key == demo.PlanKey && p.IsActive, cancellationToken)
            .ConfigureAwait(false);
        if (plan is null)
        {
            if (_logger.IsEnabled(LogLevel.Warning))
            {
                _logger.LogWarning(
                    "[demo-seed] [{Tenant}] plan '{PlanKey}' not found — skipping subscription", demo.Id, demo.PlanKey);
            }
            return;
        }

        // Reuse the existing active subscription's term if present so re-runs don't re-subscribe but
        // still backfill a missing invoice, otherwise start fresh aligned to the tenant's ValidUpto.
        var existing = await billingDb.Subscriptions
            .FirstOrDefaultAsync(s => s.TenantId == demo.Id && s.Status == SubscriptionStatus.Active, cancellationToken)
            .ConfigureAwait(false);

        var startUtc = existing?.StartUtc ?? DateTime.UtcNow;
        var endUtc = existing?.EndUtc ?? DateTime.SpecifyKind(tenant.ValidUpto, DateTimeKind.Utc);

        if (existing is null)
        {
            billingDb.Subscriptions.Add(Subscription.Create(demo.Id, plan.Id, startUtc, endUtc));
            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation(
                    "[demo-seed] [{Tenant}] subscribed to plan '{PlanKey}' (term ends {End:o})",
                    demo.Id, plan.Key, endUtc);
            }
        }

        // Paid plans get an issued term invoice (like real CreateTenant), written directly so no InvoiceIssuedIntegrationEvent fires
        // (no outbox dispatcher; seeding mustn't email). Idempotent on invoice number; free plans (term price 0) get none, as in production.
        if (plan.TermPrice.Amount > 0m)
        {
            var invoiceNumber = string.Create(
                CultureInfo.InvariantCulture, $"SUB-{startUtc:yyyyMM}-{demo.Id.ToUpperInvariant()}");
            var invoiceExists = await billingDb.Invoices
                .AnyAsync(i => i.TenantId == demo.Id && i.InvoiceNumber == invoiceNumber, cancellationToken)
                .ConfigureAwait(false);
            if (!invoiceExists)
            {
                var invoice = Invoice.CreateDraft(
                    demo.Id, invoiceNumber, startUtc.Year, startUtc.Month, plan.Currency,
                    InvoicePurpose.Subscription, startUtc, endUtc);
                invoice.AddLineItem(
                    InvoiceLineItemKind.BaseFee,
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"{plan.Name} — {plan.Interval} subscription ({startUtc:yyyy-MM-dd} to {endUtc:yyyy-MM-dd})"),
                    1m,
                    plan.TermPrice.Amount);
                invoice.Issue();
                billingDb.Invoices.Add(invoice);
                if (_logger.IsEnabled(LogLevel.Information))
                {
                    _logger.LogInformation(
                        "[demo-seed] [{Tenant}] issued term invoice {InvoiceNumber} ({Amount} {Currency})",
                        demo.Id, invoiceNumber, plan.TermPrice, plan.Currency);
                }
            }
        }

        await billingDb.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    // ─── Users + roles ─────────────────────────────────────────────────

    private async Task SeedRootSuperAdminAsync(CancellationToken cancellationToken)
    {
        var rootTenant = new AppTenantInfo(
            id: MultitenancyConstants.Root.Id,
            name: MultitenancyConstants.Root.Name,
            connectionString: string.Empty,
            adminEmail: MultitenancyConstants.Root.EmailAddress,
            issuer: MultitenancyConstants.Root.Issuer);

        await SeedUsersInTenantAsync(rootTenant, BuildRootUsers(), [], cancellationToken).ConfigureAwait(false);
    }

    private async Task SeedTenantUsersAsync(DemoTenant demo, CancellationToken cancellationToken)
    {
        using var scope = _services.CreateScope();
        var tenantStore = scope.ServiceProvider.GetRequiredService<IMultiTenantStore<AppTenantInfo>>();
        var tenant = await tenantStore.GetAsync(demo.Id).ConfigureAwait(false);
        if (tenant is null) return;

        var users = demo.Id == Acme.Id ? BuildAcmeUsers() : BuildGlobexUsers();
        var customRoles = demo.Id == Acme.Id ? BuildAcmeCustomRoles() : Array.Empty<DemoRole>();
        await SeedUsersInTenantAsync(tenant, users, customRoles, cancellationToken).ConfigureAwait(false);
    }

    private async Task SeedUsersInTenantAsync(
        AppTenantInfo tenant,
        IReadOnlyList<DemoUser> users,
        IReadOnlyList<DemoRole> customRoles,
        CancellationToken cancellationToken)
    {
        using var scope = _services.CreateScope();
        scope.ServiceProvider.GetRequiredService<IMultiTenantContextSetter>()
            .MultiTenantContext = new MultiTenantContext<AppTenantInfo>(tenant);

        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<FshUser>>();
        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<FshRole>>();
        var context = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
        var hasher = new PasswordHasher<FshUser>();

        foreach (var demoRole in customRoles)
        {
            var role = await roleManager.FindByNameAsync(demoRole.Name).ConfigureAwait(false);
            if (role is null)
            {
                role = new FshRole(demoRole.Name, demoRole.Description);
                await roleManager.CreateAsync(role).ConfigureAwait(false);
                if (_logger.IsEnabled(LogLevel.Information))
                {
                    _logger.LogInformation("[demo-seed] [{Tenant}] created custom role '{Role}'", tenant.Id, demoRole.Name);
                }
            }

            var existingClaims = await roleManager.GetClaimsAsync(role).ConfigureAwait(false);
            foreach (var permission in demoRole.Permissions)
            {
                if (existingClaims.Any(c => c.Type == ClaimConstants.Permission && c.Value == permission))
                {
                    continue;
                }
                context.RoleClaims.Add(new FshRoleClaim
                {
                    RoleId = role.Id,
                    ClaimType = ClaimConstants.Permission,
                    ClaimValue = permission,
                    CreatedBy = "DemoSeeder",
                    CreatedOn = DateTimeOffset.UtcNow,
                });
            }
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach (var demoUser in users)
        {
            var existing = await userManager.FindByEmailAsync(demoUser.Email).ConfigureAwait(false);
            if (existing is null)
            {
                var user = new FshUser
                {
                    UserName = demoUser.UserName,
                    Email = demoUser.Email,
                    EmailConfirmed = true,
                    FirstName = demoUser.FirstName,
                    LastName = demoUser.LastName,
                    IsActive = true,
                    NormalizedEmail = demoUser.Email.ToUpperInvariant(),
                    NormalizedUserName = demoUser.UserName.ToUpperInvariant(),
                };
                user.PasswordHash = hasher.HashPassword(user, _sharedPassword);
                var created = await userManager.CreateAsync(user).ConfigureAwait(false);
                if (!created.Succeeded)
                {
                    if (_logger.IsEnabled(LogLevel.Warning))
                    {
                        _logger.LogWarning(
                            "[demo-seed] [{Tenant}] failed to create '{Email}': {Errors}",
                            tenant.Id, demoUser.Email,
                            string.Join("; ", created.Errors.Select(e => e.Description)));
                    }
                    continue;
                }
                existing = user;
            }
            else
            {
                await EnsureSharedPasswordAsync(userManager, hasher, existing).ConfigureAwait(false);
            }

            foreach (var role in demoUser.Roles)
            {
                if (!await userManager.IsInRoleAsync(existing, role).ConfigureAwait(false))
                {
                    var roleEntity = await roleManager.FindByNameAsync(role).ConfigureAwait(false);
                    if (roleEntity is null) continue;
                    await userManager.AddToRoleAsync(existing, role).ConfigureAwait(false);
                }
            }
        }

        // Tenant admin (admin@<tenant>.com) was created by IdentityDbInitializer with the framework default password.
        // Realign it to the shared password so the dev login panel's advertised credential is truthful.
        if (!string.IsNullOrWhiteSpace(tenant.AdminEmail))
        {
            var admin = await userManager.FindByEmailAsync(tenant.AdminEmail).ConfigureAwait(false);
            if (admin is not null)
            {
                await EnsureSharedPasswordAsync(userManager, hasher, admin).ConfigureAwait(false);
            }
        }
    }

    private async Task EnsureSharedPasswordAsync(
        UserManager<FshUser> userManager,
        PasswordHasher<FshUser> hasher,
        FshUser user)
    {
        if (await userManager.CheckPasswordAsync(user, _sharedPassword).ConfigureAwait(false))
        {
            return;
        }
        user.PasswordHash = hasher.HashPassword(user, _sharedPassword);
        var result = await userManager.UpdateAsync(user).ConfigureAwait(false);
        if (!result.Succeeded && _logger.IsEnabled(LogLevel.Warning))
        {
            _logger.LogWarning(
                "[demo-seed] failed to reset password for '{Email}': {Errors}",
                user.Email,
                string.Join("; ", result.Errors.Select(e => e.Description)));
        }
    }

    // ─── Demo content shapes ───────────────────────────────────────────

    internal sealed record DemoTenant(string Id, string Name, string AdminEmail, string Issuer, string PlanKey);
    internal sealed record DemoUser(
        string UserName,
        string Email,
        string FirstName,
        string LastName,
        IReadOnlyList<string> Roles);
    internal sealed record DemoRole(string Name, string Description, IReadOnlyList<string> Permissions);

    private static IReadOnlyList<DemoUser> BuildRootUsers() =>
    [
        new("superadmin", "superadmin@root.com", "Super", "Admin", [RoleConstants.Admin]),
    ];

    private static IReadOnlyList<DemoUser> BuildAcmeUsers() =>
    [
        new("acme.manager",  "manager@acme.com",  "Maya",   "Lin",      ["Manager"]),
        new("acme.support",  "support@acme.com",  "Sam",    "Rivera",   ["Support"]),
        new("acme.alice",    "alice@acme.com",    "Alice",  "Nguyen",   [RoleConstants.Basic]),
        new("acme.bob",      "bob@acme.com",      "Bob",    "Patel",    [RoleConstants.Basic]),
        new("acme.carol",    "carol@acme.com",    "Carol",  "Smith",    [RoleConstants.Basic]),
        new("acme.dan",      "dan@acme.com",      "Dan",    "Mueller",  [RoleConstants.Basic]),
        new("acme.erin",     "erin@acme.com",     "Erin",   "Okafor",   [RoleConstants.Basic]),
        new("acme.frank",    "frank@acme.com",    "Frank",  "Tanaka",   [RoleConstants.Basic]),
        new("acme.gina",     "gina@acme.com",     "Gina",   "Kowalski", [RoleConstants.Basic]),
        new("acme.henry",    "henry@acme.com",    "Henry",  "Park",     [RoleConstants.Basic]),
    ];

    private static IReadOnlyList<DemoUser> BuildGlobexUsers() =>
    [
        new("globex.dave",   "dave@globex.com",   "Dave",   "Hartwell", [RoleConstants.Basic]),
    ];

    // Permission claims reference the module contracts constants — never raw strings.
    // A hand-typed name that doesn't match a registry entry (e.g. the old
    // "Permissions.Session.View" vs the real "Permissions.Sessions.View")
    // is a claim that grants nothing, silently.
    private static IReadOnlyList<DemoRole> BuildAcmeCustomRoles() =>
    [
        new(
            "Manager",
            // The kit's Users, Roles and Groups administration is root-only in the PAF (hidden from the
            // client panel): granting them here would be written straight to the table and then stripped by the API's
            // role sync on the next start.
            "Operations manager — own sessions.",
            [
                IdentityPermissions.UserRoles.View,
                IdentityPermissions.Sessions.View,
                IdentityPermissions.Sessions.Revoke,
            ]),

        new(
            "Support",
            "Support agent — own sessions.",
            [
                IdentityPermissions.UserRoles.View,
                IdentityPermissions.Sessions.View,
                IdentityPermissions.Sessions.Revoke,
            ]),
    ];
}
