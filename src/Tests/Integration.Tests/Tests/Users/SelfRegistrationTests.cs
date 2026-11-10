using Finbuckle.MultiTenant;
using Finbuckle.MultiTenant.Abstractions;
using FSH.Framework.Eventing.Persistence;
using FSH.Framework.Shared.Multitenancy;
using Integration.Tests.Infrastructure;

namespace Integration.Tests.Tests.Users;

/// <summary>
/// The kit's anonymous self-register is unmapped in the PAF (C7): accounts are created by an admin only. The admin
/// registration at <c>/register</c> stays and is covered by <see cref="UserRegisteredOutboxTests"/>.
/// </summary>
[Collection(FshCollectionDefinition.Name)]
public sealed class SelfRegistrationTests
{
    private readonly FshWebApplicationFactory _factory;
    private readonly AuthHelper _auth;

    public SelfRegistrationTests(FshWebApplicationFactory factory)
    {
        _factory = factory;
        _auth = new AuthHelper(factory);
    }

    [Fact]
    public async Task SelfRegister_Anonymous_CreatesNoAccount_InRootNorInAClientTenant()
    {
        foreach (var tenantId in new[] { TestConstants.RootTenantId, await ProvisionClientTenantAsync() })
        {
            var handle = $"self-{Guid.NewGuid():N}"[..16];
            var email = $"{handle}@example.com";
            using var client = _factory.CreateClient();
            client.DefaultRequestHeaders.Add("tenant", tenantId);

            using var response = await client.PostAsJsonAsync($"{TestConstants.IdentityBasePath}/self-register", SelfRegisterBody(handle, email));

            // No endpoint matches, so the fallback authorization policy challenges the anonymous caller before the 404.
            response.StatusCode.ShouldBeOneOf([HttpStatusCode.Unauthorized, HttpStatusCode.NotFound], await response.Content.ReadAsStringAsync());
            (await UserRowsWithEmailAsync(email)).ShouldBe(0, $"no account may exist in any tenant (asked in {tenantId})");
        }
    }

    [Fact]
    public async Task SelfRegister_Should_Return404_ForAnAuthenticatedCaller_BecauseTheRouteIsGone()
    {
        // Past authentication, only routing answers: 404 proves the route is unmapped, not merely guarded.
        using var root = await _auth.CreateRootAdminClientAsync();
        var handle = $"self-{Guid.NewGuid():N}"[..16];
        var email = $"{handle}@example.com";

        using var response = await root.PostAsJsonAsync($"{TestConstants.IdentityBasePath}/self-register", SelfRegisterBody(handle, email));

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound, await response.Content.ReadAsStringAsync());
        (await UserRowsWithEmailAsync(email)).ShouldBe(0);
    }

    private static object SelfRegisterBody(string handle, string email) => new
    {
        firstName = "Self",
        lastName = "Reg",
        email,
        userName = handle,
        password = TestConstants.DefaultPassword,
        confirmPassword = TestConstants.DefaultPassword,
    };

    private async Task<string> ProvisionClientTenantAsync()
    {
        using var root = await _auth.CreateRootAdminClientAsync();
        var uniqueId = Guid.NewGuid().ToString("N")[..8];
        var tenantId = $"selfreg-{uniqueId}";
        using var created = await root.PostAsJsonAsync(TestConstants.TenantsBasePath, new
        {
            id = tenantId,
            name = $"Tenant {tenantId}",
            connectionString = (string?)null,
            adminEmail = $"selfreg-{uniqueId}@tenant.com",
            adminPassword = TestConstants.DefaultPassword,
            issuer = $"{tenantId}.issuer"
        });
        created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        await TenantProvisioningWait.WaitForProvisioningAsync(root, tenantId);
        return tenantId;
    }

    /// <summary>
    /// Users with <paramref name="email"/> in any tenant. Raw SQL on purpose: the tenant filter would hide an account
    /// written into a tenant other than the one the request named.
    /// </summary>
    private async Task<int> UserRowsWithEmailAsync(string email)
    {
        using var scope = _factory.Services.CreateScope();
        var tenant = await scope.ServiceProvider.GetRequiredService<IMultiTenantStore<AppTenantInfo>>().GetAsync(TestConstants.RootTenantId);
        tenant.ShouldNotBeNull();
        scope.ServiceProvider.GetRequiredService<IMultiTenantContextSetter>()
            .MultiTenantContext = new MultiTenantContext<AppTenantInfo>(tenant);
        var db = scope.ServiceProvider.GetRequiredService<EventingDbContext>();

        return await db.Database
            .SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM identity.\"Users\" WHERE \"Email\" = {0}", email)
            .SingleAsync();
    }
}
