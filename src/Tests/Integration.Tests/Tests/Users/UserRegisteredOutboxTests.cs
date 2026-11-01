using Finbuckle.MultiTenant;
using Finbuckle.MultiTenant.Abstractions;
using FSH.Framework.Eventing.Persistence;
using FSH.Framework.Shared.Multitenancy;
using FSH.Modules.Identity.Contracts.Events;
using Integration.Tests.Infrastructure;
using Integration.Tests.Infrastructure.Extensions;

namespace Integration.Tests.Tests.Users;

/// <summary>
/// Registration used to write two UserRegisteredIntegrationEvent rows with different ids (one from
/// the domain-event handler, one straight from the registration service), so the inbox could not
/// deduplicate them and every new user got the welcome e-mail twice. Counts the real outbox rows.
/// </summary>
[Collection(FshCollectionDefinition.Name)]
public sealed class UserRegisteredOutboxTests
{
    private readonly FshWebApplicationFactory _factory;
    private readonly AuthHelper _auth;

    public UserRegisteredOutboxTests(FshWebApplicationFactory factory)
    {
        _factory = factory;
        _auth = new AuthHelper(factory);
    }

    [Fact]
    public async Task Register_Should_WriteExactlyOneUserRegisteredEvent_When_AdminRegistersUser()
    {
        using var client = await _auth.CreateRootAdminClientAsync();
        var uniqueId = Guid.NewGuid().ToString("N")[..8];

        var response = await client.PostAsJsonAsync($"{TestConstants.IdentityBasePath}/register", new
        {
            firstName = "Outbox",
            lastName = "Admin",
            email = $"outbox-admin-{uniqueId}@example.com",
            userName = $"outboxadmin-{uniqueId}",
            password = "Test@1234!",
            confirmPassword = "Test@1234!"
        });

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var result = await response.DeserializeAsync<RegisterResult>();

        (await CountUserRegisteredRowsAsync(result.UserId)).ShouldBe(1);
    }

    [Fact]
    public async Task SelfRegister_Should_WriteExactlyOneUserRegisteredEvent_When_AnonymousRegisters()
    {
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("tenant", TestConstants.RootTenantId);
        var uniqueId = Guid.NewGuid().ToString("N")[..8];

        var response = await client.PostAsJsonAsync($"{TestConstants.IdentityBasePath}/self-register", new
        {
            firstName = "Outbox",
            lastName = "Self",
            email = $"outbox-self-{uniqueId}@example.com",
            userName = $"outboxself-{uniqueId}",
            password = "Test@1234!",
            confirmPassword = "Test@1234!"
        });

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var result = await response.DeserializeAsync<RegisterResult>();

        (await CountUserRegisteredRowsAsync(result.UserId)).ShouldBe(1);
    }

    // ProcessedOnUtc is ignored on purpose: another test's OutboxDrain may already have dispatched the
    // row, and a dispatched duplicate still sends a second e-mail.
    private async Task<int> CountUserRegisteredRowsAsync(string userId)
    {
        using var scope = _factory.Services.CreateScope();
        var tenant = await scope.ServiceProvider
            .GetRequiredService<IMultiTenantStore<AppTenantInfo>>()
            .GetAsync(TestConstants.RootTenantId);
        scope.ServiceProvider.GetRequiredService<IMultiTenantContextSetter>()
            .MultiTenantContext = new MultiTenantContext<AppTenantInfo>(tenant);
        var context = scope.ServiceProvider.GetRequiredService<EventingDbContext>();

        return await context.OutboxMessages.CountAsync(m =>
            m.Type.Contains(nameof(UserRegisteredIntegrationEvent)) && m.Payload.Contains(userId));
    }
}
