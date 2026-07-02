using FSH.Framework.Mailing;
using FSH.Framework.Mailing.Services;
using Integration.Tests.Infrastructure;
using Integration.Tests.Tests.Sessions;

namespace Integration.Tests.Tests.Users;

/// <summary>
/// Proves that the actual e-mail the app renders carries a link based on the front-end origin the request
/// came from (validated Origin header), through the real forgot-password / register → resolver → link-build
/// → mail pipeline. Mail is captured by NoOpMailService; dispatch is a Hangfire job, so we poll.
/// </summary>
[Collection(FshCollectionDefinition.Name)]
public sealed class EmailLinkOriginTests
{
    private readonly FshWebApplicationFactory _factory;
    private readonly AuthHelper _auth;

    public EmailLinkOriginTests(FshWebApplicationFactory factory)
    {
        _factory = factory;
        _auth = new AuthHelper(factory);
    }

    private NoOpMailService Mail => (NoOpMailService)_factory.Services.GetRequiredService<IMailService>();

    private static async Task<MailRequest> WaitForMailAsync(NoOpMailService mail, Func<MailRequest, bool> match)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var hit = mail.Sent.FirstOrDefault(match);
            if (hit is not null)
            {
                return hit;
            }

            await Task.Delay(150);
        }

        throw new Xunit.Sdk.XunitException("Expected e-mail was not captured within the timeout.");
    }

    [Fact]
    public async Task ForgotPassword_Should_EmitResetLink_ToRequestingFrontend()
    {
        // Arrange
        using var adminClient = await _auth.CreateRootAdminClientAsync();
        var user = await IdentityUserSeeder.CreateLoginableUserAsync(_factory, adminClient, "reset-5174");
        Mail.Clear();

        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("tenant", TestConstants.RootTenantId);
        client.DefaultRequestHeaders.Remove("Origin");
        client.DefaultRequestHeaders.Add("Origin", "http://localhost:5174");

        // Act
        var response = await client.PostAsJsonAsync(
            $"{TestConstants.IdentityBasePath}/forgot-password", new { email = user.Email });
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        // Assert - the rendered e-mail links to the :5174 SPA reset page with the required params.
        var mail = await WaitForMailAsync(Mail, m => m.To.Contains(user.Email));
        var body = mail.Body.ShouldNotBeNull();
        body.ShouldContain("http://localhost:5174/reset-password");
        body.ShouldContain($"tenant={TestConstants.RootTenantId}");
        body.ShouldNotContain(":7030");
    }

    [Fact]
    public async Task ForgotPassword_Should_EmitResetLink_ToTheOtherFrontend()
    {
        // Arrange - a request from the admin SPA (:5173) must resolve to :5173, proving per-front resolution.
        using var adminClient = await _auth.CreateRootAdminClientAsync();
        var user = await IdentityUserSeeder.CreateLoginableUserAsync(_factory, adminClient, "reset-5173");
        Mail.Clear();

        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("tenant", TestConstants.RootTenantId);
        client.DefaultRequestHeaders.Remove("Origin");
        client.DefaultRequestHeaders.Add("Origin", "http://localhost:5173");

        // Act
        var response = await client.PostAsJsonAsync(
            $"{TestConstants.IdentityBasePath}/forgot-password", new { email = user.Email });
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        // Assert
        var mail = await WaitForMailAsync(Mail, m => m.To.Contains(user.Email));
        mail.Body.ShouldNotBeNull().ShouldContain("http://localhost:5173/reset-password");
    }

    [Fact]
    public async Task Register_Should_EmitConfirmationLink_ToRequestingFrontend()
    {
        // Arrange
        using var adminClient = await _auth.CreateRootAdminClientAsync();
        adminClient.DefaultRequestHeaders.Remove("Origin");
        adminClient.DefaultRequestHeaders.Add("Origin", "http://localhost:5174");
        Mail.Clear();
        var uniqueId = Guid.NewGuid().ToString("N")[..8];
        var email = $"confirm-{uniqueId}@example.com";

        // Act
        var response = await adminClient.PostAsJsonAsync($"{TestConstants.IdentityBasePath}/register", new
        {
            firstName = "Confirm",
            lastName = "Link",
            email,
            userName = $"confirm-{uniqueId}",
            password = "Test@1234!",
            confirmPassword = "Test@1234!"
        });
        response.StatusCode.ShouldBe(HttpStatusCode.Created);

        // Assert - confirmation e-mail links to the SPA confirm-email page, not the API route.
        var mail = await WaitForMailAsync(Mail, m => m.To.Contains(email));
        var body = mail.Body.ShouldNotBeNull();
        body.ShouldContain("http://localhost:5174/confirm-email");
        body.ShouldContain("userId=");
        body.ShouldContain("code=");
        body.ShouldNotContain("api/v1/identity/confirm-email");
    }
}
