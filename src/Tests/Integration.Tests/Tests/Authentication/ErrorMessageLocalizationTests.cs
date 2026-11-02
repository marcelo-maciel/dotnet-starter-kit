using System.Text.Json;
using Finbuckle.MultiTenant;
using Finbuckle.MultiTenant.Abstractions;
using FSH.Framework.Shared.Multitenancy;
using FSH.Modules.Identity.Domain;
using Integration.Tests.Infrastructure;
using Microsoft.AspNetCore.Identity;

namespace Integration.Tests.Tests.Authentication;

/// <summary>
/// The texts a tenant user reads on login, change password and forgot password come out in the request's
/// culture through the real pipeline: the ASP.NET Identity errors list (LocalizedIdentityErrorDescriber),
/// FluentValidation messages, built-in and keyed, and a handler's success string. Each case runs under pt-BR
/// (Accept-Language) and under the default culture (English), so neither branch can regress silently.
/// The exception Detail is not asserted here: this host swaps GlobalExceptionHandler for
/// DetailedTestExceptionHandler, which writes the raw message; Integration.Middleware.Tests covers it.
/// </summary>
[Collection(FshCollectionDefinition.Name)]
public sealed class ErrorMessageLocalizationTests
{
    private readonly FshWebApplicationFactory _factory;
    private readonly AuthHelper _auth;

    public ErrorMessageLocalizationTests(FshWebApplicationFactory factory)
    {
        _factory = factory;
        _auth = new AuthHelper(factory);
    }

    [Theory]
    [InlineData("pt-BR", "'Email' deve ser informado.")]
    [InlineData(null, "'Email' must not be empty.")]
    public async Task Login_without_email_reports_the_builtin_validation_in_the_request_culture(string? acceptLanguage, string message)
    {
        // Arrange
        using var client = AnonymousClient(acceptLanguage);

        // Act
        using var response = await client.PostAsJsonAsync(
            $"{TestConstants.IdentityBasePath}/token/issue",
            new { email = "", password = "SenhaErrada123!" });

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        using var body = await ProblemAsync(response);
        FirstError(body, "Email").ShouldBe(message);
    }

    [Theory]
    [InlineData("pt-BR", "Senha incorreta.")]
    [InlineData(null, "Incorrect password.")]
    public async Task Change_password_with_wrong_current_reports_identity_errors_in_the_request_culture(
        string? acceptLanguage, string identityError)
    {
        // Arrange
        var email = $"msg_wrongpw_{Guid.NewGuid():N}@test.com";
        await CreateActiveUserAsync(email);
        using var client = await AuthenticatedClientAsync(email, acceptLanguage);

        // Act
        using var response = await client.PostAsJsonAsync(
            $"{TestConstants.IdentityBasePath}/change-password",
            new { password = "AtualErrada123!", newPassword = "NovaSenha456!", confirmNewPassword = "NovaSenha456!" });

        // Assert
        ((int)response.StatusCode).ShouldBeGreaterThanOrEqualTo(400);
        using var body = await ProblemAsync(response);
        body.RootElement.GetProperty("errors").EnumerateArray().Select(e => e.GetString()).ShouldContain(identityError);
    }

    [Theory]
    [InlineData("pt-BR", "A nova senha deve ser diferente da senha atual.")]
    [InlineData(null, "New password must be different from the current password.")]
    public async Task Change_password_to_the_same_value_reports_the_keyed_validation_in_the_request_culture(
        string? acceptLanguage, string message)
    {
        // Arrange
        var email = $"msg_samepw_{Guid.NewGuid():N}@test.com";
        await CreateActiveUserAsync(email);
        using var client = await AuthenticatedClientAsync(email, acceptLanguage);

        // Act
        using var response = await client.PostAsJsonAsync(
            $"{TestConstants.IdentityBasePath}/change-password",
            new
            {
                password = TestConstants.DefaultPassword,
                newPassword = TestConstants.DefaultPassword,
                confirmNewPassword = TestConstants.DefaultPassword,
            });

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        using var body = await ProblemAsync(response);
        body.RootElement.GetProperty("errors").GetProperty("NewPassword")
            .EnumerateArray().Select(e => e.GetString()).ShouldContain(message);
    }

    [Theory]
    [InlineData("pt-BR", "E-mail de redefinição de senha enviado.")]
    [InlineData(null, "Password reset email sent.")]
    public async Task Forgot_password_confirms_in_the_request_culture(string? acceptLanguage, string message)
    {
        // Arrange: an unknown address answers exactly like a known one (anti-enumeration), with no mail sent.
        using var client = AnonymousClient(acceptLanguage);

        // Act
        using var response = await client.PostAsJsonAsync(
            $"{TestConstants.IdentityBasePath}/forgot-password",
            new { email = $"ninguem_{Guid.NewGuid():N}@test.com" });

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<string>()).ShouldBe(message);
    }

    // ─── helpers ────────────────────────────────────────────────────────

    private HttpClient AnonymousClient(string? acceptLanguage)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("tenant", TestConstants.RootTenantId);
        AddAcceptLanguage(client, acceptLanguage);
        return client;
    }

    private async Task<HttpClient> AuthenticatedClientAsync(string email, string? acceptLanguage)
    {
        var client = await _auth.CreateAuthenticatedClientAsync(email, TestConstants.DefaultPassword, TestConstants.RootTenantId);
        AddAcceptLanguage(client, acceptLanguage);
        return client;
    }

    private static void AddAcceptLanguage(HttpClient client, string? acceptLanguage)
    {
        if (acceptLanguage is not null)
        {
            client.DefaultRequestHeaders.Add("Accept-Language", acceptLanguage);
        }
    }

    // The body escapes non-ASCII characters, so texts are compared after parsing, never as raw JSON.
    private static async Task<JsonDocument> ProblemAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync());

    private static string? FirstError(JsonDocument body, string property) =>
        body.RootElement.GetProperty("errors").GetProperty(property)[0].GetString();

    // No Locale on the account: the user-locale claim would otherwise outrank Accept-Language.
    private async Task CreateActiveUserAsync(string email)
    {
        using var scope = _factory.Services.CreateScope();
        var tenant = await scope.ServiceProvider
            .GetRequiredService<IMultiTenantStore<AppTenantInfo>>().GetAsync(TestConstants.RootTenantId);
        scope.ServiceProvider.GetRequiredService<IMultiTenantContextSetter>()
            .MultiTenantContext = new MultiTenantContext<AppTenantInfo>(tenant);

        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<FshUser>>();
        var user = new FshUser
        {
            FirstName = "Mensagem",
            LastName = "Teste",
            Email = email,
            UserName = email.Split('@')[0],
            EmailConfirmed = true,
            IsActive = true,
        };
        var result = await userManager.CreateAsync(user, TestConstants.DefaultPassword);
        result.Succeeded.ShouldBeTrue(string.Join("; ", result.Errors.Select(e => e.Description)));
    }
}
