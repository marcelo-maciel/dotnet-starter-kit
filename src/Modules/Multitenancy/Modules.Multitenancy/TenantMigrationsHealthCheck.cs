using Finbuckle.MultiTenant;
using Finbuckle.MultiTenant.Abstractions;
using FSH.Framework.Shared.Multitenancy;
using FSH.Modules.Multitenancy.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;

namespace FSH.Modules.Multitenancy;

/// <summary>
/// Readiness check: returns <c>Unhealthy</c> when any tenant's <c>TenantDbContext</c> has
/// pending EF Core migrations, or when the per-tenant migration probe throws. Wired into
/// <c>/health/ready</c>, which Kubernetes / load-balancer readiness probes key off — so
/// a pod whose schema is behind the running build is kept out of rotation until the
/// standalone <c>FSH.Starter.DbMigrator</c> catches it up.
/// </summary>
public sealed class TenantMigrationsHealthCheck : IHealthCheck
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<TenantMigrationsHealthCheck> _logger;

    public TenantMigrationsHealthCheck(IServiceScopeFactory scopeFactory, ILogger<TenantMigrationsHealthCheck> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        using IServiceScope scope = _scopeFactory.CreateScope();

        var tenantStore = scope.ServiceProvider.GetRequiredService<IMultiTenantStore<AppTenantInfo>>();
        var tenants = await tenantStore.GetAllAsync().ConfigureAwait(false);

        int total = 0;
        int withPending = 0;
        int withError = 0;

        foreach (var tenant in tenants)
        {
            total++;
            try
            {
                using IServiceScope tenantScope = scope.ServiceProvider.CreateScope();

                tenantScope.ServiceProvider.GetRequiredService<IMultiTenantContextSetter>()
                    .MultiTenantContext = new MultiTenantContext<AppTenantInfo>(tenant);

                var dbContext = tenantScope.ServiceProvider.GetRequiredService<TenantDbContext>();

                var pendingMigrations = (await dbContext.Database
                    .GetPendingMigrationsAsync(cancellationToken)
                    .ConfigureAwait(false))
                    .ToArray();

                if (pendingMigrations.Length > 0)
                {
                    withPending++;
                    _logger.LogWarning("Tenant {TenantId} has {PendingCount} pending migration(s): {PendingMigrations}",
                        tenant.Id, pendingMigrations.Length, string.Join(", ", pendingMigrations));
                }
            }
            // Health checks must report errors, not throw. Which tenant failed, and why, goes to the server log only.
            catch (Exception ex)
            {
                withError++;
                _logger.LogError(ex, "Migration probe failed for tenant {TenantId}", tenant.Id);
            }
        }

        // /health/ready is anonymous: the payload carries counts only, never a tenant's id, name, state, validity
        // or exception text, so a stranger cannot list the clients nor learn when one expires.
        var details = new Dictionary<string, object>
        {
            ["TenantsTotal"] = total,
            ["TenantsWithPendingMigrations"] = withPending,
            ["TenantsWithError"] = withError
        };

        if (withError > 0 || withPending > 0)
        {
            return HealthCheckResult.Unhealthy(
                $"Tenant schema is not at head: {withPending} tenant(s) with pending migrations, {withError} with probe error. " +
                "Run FSH.Starter.DbMigrator to apply pending migrations; the server log names the tenants.",
                data: details);
        }

        return HealthCheckResult.Healthy("All tenants are at the head migration.", details);
    }
}
