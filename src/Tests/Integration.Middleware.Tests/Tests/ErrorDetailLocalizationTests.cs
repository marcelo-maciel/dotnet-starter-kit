using System.Text.Json;
using Integration.Middleware.Tests.Infrastructure;

namespace Integration.Middleware.Tests.Tests;

/// <summary>
/// The ProblemDetails Detail of a keyed exception is translated by the production GlobalExceptionHandler, which
/// only this host keeps (Integration.Tests swaps in a handler that writes the raw message). Pins the login body a
/// tenant user reads to the request culture: pt-BR via Accept-Language, English by default. Anonymous calls only:
/// this host keeps the real auth rate limit, and the seeded admin's locale claim would outrank Accept-Language.
/// </summary>
[Collection(MiddlewareCollectionDefinition.Name)]
public sealed class ErrorDetailLocalizationTests
{
    private readonly MiddlewareWebApplicationFactory _factory;

    public ErrorDetailLocalizationTests(MiddlewareWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Theory]
    [InlineData("pt-BR", "Não autorizado", "Falha na autenticação.")]
    [InlineData(null, "Unauthorized", "Authentication failed.")]
    public async Task Login_with_wrong_password_reports_in_the_request_culture(string? acceptLanguage, string title, string detail)
    {
        // Arrange
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("tenant", TestConstants.RootTenantId);
        if (acceptLanguage is not null)
        {
            client.DefaultRequestHeaders.Add("Accept-Language", acceptLanguage);
        }

        // Act
        using var response = await client.PostAsJsonAsync(
            $"{TestConstants.IdentityBasePath}/token/issue",
            new { email = TestConstants.RootAdminEmail, password = "SenhaErrada123!" });

        // Assert: the body escapes non-ASCII characters, so texts are compared after parsing.
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("title").GetString().ShouldBe(title, body.RootElement.GetRawText());
        body.RootElement.GetProperty("detail").GetString().ShouldBe(detail, body.RootElement.GetRawText());
        body.RootElement.GetProperty("code").GetString().ShouldBe("Error.AuthenticationFailed");
    }
}
