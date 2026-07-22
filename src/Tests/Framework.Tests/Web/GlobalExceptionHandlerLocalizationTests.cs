using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using FSH.Framework.Core.Exceptions;
using FSH.Framework.Web.Exceptions;
using Framework.Tests.Localization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace Framework.Tests.Web;

// Handler-level (Docker-free) proof that GlobalExceptionHandler localizes the ProblemDetails body
// from the shared resx under the ambient UI culture. Covers both the framework branches (raw
// KeyNotFoundException/InvalidOperationException) and the CustomException branch, whose Title now
// comes from the status-mapped catalog key and whose Detail is resolved from MessageKey (falling
// back to the English Message when no key is set).
public sealed class GlobalExceptionHandlerLocalizationTests
{
    private static async Task<(string? Title, string? Detail)> HandleAsync(Exception exception, string culture)
    {
        var previous = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = new CultureInfo(culture);

            var context = new DefaultHttpContext();
            context.Request.Path = "/api/v1/test";
            using var body = new MemoryStream();
            context.Response.Body = body;

            var handler = new GlobalExceptionHandler(
                NullLogger<GlobalExceptionHandler>.Instance,
                SharedResourcesLocalizerFactory.Create(),
                SharedResourcesLocalizerFactory.CreateFactory());
            await handler.TryHandleAsync(context, exception, CancellationToken.None);

            var json = Encoding.UTF8.GetString(body.ToArray());
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var title = root.TryGetProperty("title", out var t) ? t.GetString() : null;
            var detail = root.TryGetProperty("detail", out var d) ? d.GetString() : null;
            return (title, detail);
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
        }
    }

    [Theory]
    [InlineData("pt-BR", "Não encontrado")]
    [InlineData("en-US", "Not Found")]
    public async Task NotFound_title_is_localized(string culture, string expected)
    {
        var (title, _) = await HandleAsync(new KeyNotFoundException("missing"), culture);
        title.ShouldBe(expected);
    }

    [Theory]
    [InlineData("pt-BR", "Ocorreu um erro inesperado")]
    [InlineData("en-US", "An unexpected error occurred")]
    public async Task Unexpected_title_is_localized(string culture, string expected)
    {
        var (title, _) = await HandleAsync(new InvalidOperationException("boom"), culture);
        title.ShouldBe(expected);
    }

    // CustomException Title is now the status-mapped catalog key (404 -> Error.NotFound), localized.
    [Theory]
    [InlineData("pt-BR", "Não encontrado")]
    [InlineData("en-US", "Not Found")]
    public async Task CustomException_title_is_localized_by_status(string culture, string expected)
    {
        var (title, _) = await HandleAsync(new NotFoundException("some entity was not found"), culture);
        title.ShouldBe(expected);
    }

    // Detail resolves from MessageKey under the request culture (using an existing Core key).
    [Theory]
    [InlineData("pt-BR", "Não autorizado")]
    [InlineData("en-US", "Unauthorized")]
    public async Task CustomException_detail_is_localized_from_key(string culture, string expected)
    {
        var exception = new UnauthorizedException("english fallback") { MessageKey = "Error.Unauthorized" };
        var (_, detail) = await HandleAsync(exception, culture);
        detail.ShouldBe(expected);
    }

    // No MessageKey: Detail falls back to the literal (English) Message regardless of culture (non-breaking).
    [Theory]
    [InlineData("pt-BR")]
    [InlineData("en-US")]
    public async Task CustomException_detail_falls_back_to_message_without_key(string culture)
    {
        var (_, detail) = await HandleAsync(new NotFoundException("Plain English detail."), culture);
        detail.ShouldBe("Plain English detail.");
    }

    // Parameterless UnauthorizedException carries Error.AuthenticationFailed, so generic auth failures
    // localize their Detail without any call-site key (English fallback stays "Authentication failed.").
    [Theory]
    [InlineData("pt-BR", "Falha na autenticação.")]
    [InlineData("en-US", "Authentication failed.")]
    public async Task Parameterless_unauthorized_detail_is_localized(string culture, string expected)
    {
        var (_, detail) = await HandleAsync(new UnauthorizedException(), culture);
        detail.ShouldBe(expected);
    }

    // Unknown MessageKey: ResourceNotFound path falls back to the English Message, never leaks the raw key.
    [Fact]
    public async Task CustomException_detail_falls_back_when_key_missing()
    {
        var exception = new NotFoundException("English fallback detail.") { MessageKey = "Does.Not.Exist" };
        var (_, detail) = await HandleAsync(exception, "pt-BR");
        detail.ShouldBe("English fallback detail.");
    }
}
