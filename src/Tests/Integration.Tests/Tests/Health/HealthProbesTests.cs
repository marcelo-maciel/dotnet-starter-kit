using Finbuckle.MultiTenant.Abstractions;
using Finbuckle.MultiTenant.Stores;
using FSH.Framework.Shared.Multitenancy;
using Integration.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Integration.Tests.Tests.Health;

[Collection(FshCollectionDefinition.Name)]
public sealed class HealthProbesTests
{
    private readonly FshWebApplicationFactory _factory;
    private readonly AuthHelper _auth;

    public HealthProbesTests(FshWebApplicationFactory factory)
    {
        _factory = factory;
        _auth = new AuthHelper(factory);
    }

    [Fact]
    public async Task Readiness_Should_NotPublishAnyTenant_EvenAnExpiredOne_ToAnAnonymousCaller()
    {
        // Arrange: a client tenant whose validity already ended, the case an outsider would most like to learn.
        var uniqueId = Guid.NewGuid().ToString("N")[..8];
        var tenantId = $"saude-{uniqueId}";
        var tenantName = $"Empresa Vencida {uniqueId}";
        var adminEmail = $"saude-{uniqueId}@tenant.com";
        using (var rootClient = await _auth.CreateRootAdminClientAsync())
        {
            using var created = await rootClient.PostAsJsonAsync(TestConstants.TenantsBasePath, new
            {
                id = tenantId,
                name = tenantName,
                connectionString = (string?)null,
                adminEmail,
                adminPassword = TestConstants.DefaultPassword,
                issuer = $"{tenantId}.issuer"
            });
            created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
            await TenantProvisioningWait.WaitForProvisioningAsync(rootClient, tenantId);
        }
        await ExpireTenantAsync(tenantId);

        // Act
        using var client = _factory.CreateClient();
        using var response = await client.GetAsync("/health/ready");
        var body = await response.Content.ReadAsStringAsync();

        // Assert: still ready (an expired client is not an outage), and the body carries counts only.
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body);
        body.ShouldContain("\"TenantsWithError\"");
        body.ShouldNotContain("TenantsTotal", Case.Insensitive);
        body.ShouldNotContain(tenantId, Case.Insensitive);
        body.ShouldNotContain(tenantName, Case.Insensitive);
        body.ShouldNotContain(adminEmail, Case.Insensitive);
        body.ShouldNotContain(MultitenancyConstants.Root.Id, Case.Insensitive);
        body.ShouldNotContain(MultitenancyConstants.Root.EmailAddress, Case.Insensitive);
        body.ShouldNotContain("validUpto", Case.Insensitive);
        body.ShouldNotContain("isActive", Case.Insensitive);
        body.ShouldNotContain("\"error\"", Case.Insensitive);
    }

    // Straight to the EF store (SetValidity refuses a past date) and into the distributed cache store, which would
    // otherwise serve the provisioning-time copy to the health check's GetAllAsync.
    private async Task ExpireTenantAsync(string tenantId)
    {
        using var scope = _factory.Services.CreateScope();
        var stores = scope.ServiceProvider.GetServices<IMultiTenantStore<AppTenantInfo>>().ToList();
        var efStore = stores.First(s => s.GetType().Name.StartsWith("EFCoreStore", StringComparison.Ordinal));
        var tenant = await efStore.GetAsync(tenantId);
        tenant.ShouldNotBeNull();
        tenant.ValidUpto = DateTime.SpecifyKind(DateTime.UtcNow.AddDays(-30), DateTimeKind.Utc);
        (await efStore.UpdateAsync(tenant)).ShouldBeTrue();

        var cacheStore = stores.FirstOrDefault(s => s.GetType() == typeof(DistributedCacheStore<AppTenantInfo>));
        if (cacheStore is not null)
        {
            await cacheStore.UpdateAsync(tenant);
        }
    }

    [Fact]
    public async Task Liveness_Should_Return200_WithoutDependencyChecks()
    {
        using var client = _factory.CreateClient();
        var response = await client.GetAsync("/health/live");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.ShouldContain("\"status\":\"Healthy\"");
    }

    [Fact]
    public async Task Readiness_Should_Return200_When_DependenciesHealthy()
    {
        using var client = _factory.CreateClient();
        var response = await client.GetAsync("/health/ready");

        // All registered health checks should pass in the integration test setup
        // (PostgreSQL testcontainer is up, Hangfire in-memory storage is running).
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Liveness_Should_NotRequireAuthentication()
    {
        // Probes run anonymously for orchestrators (k8s, ECS) — they can't hold tokens.
        using var client = _factory.CreateClient();
        var response = await client.GetAsync("/health/live");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }
}
