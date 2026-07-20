using System.Globalization;
using System.Text;
using System.Text.Json;
using FSH.Framework.Web.Exceptions;
using Framework.Tests.Localization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace Framework.Tests.Web;

// Handler-level (Docker-free) proof that GlobalExceptionHandler localizes ProblemDetails titles
// from the shared resx under the ambient UI culture. A raw KeyNotFoundException hits the 404 branch
// whose Title is a catalog string (FSH NotFoundException is a CustomException and instead renders its
// own type name, so it is intentionally not used here).
public sealed class GlobalExceptionHandlerLocalizationTests
{
    private static async Task<string?> HandleAndReadTitleAsync(Exception exception, string culture)
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
                SharedResourcesLocalizerFactory.Create());
            await handler.TryHandleAsync(context, exception, CancellationToken.None);

            var json = Encoding.UTF8.GetString(body.ToArray());
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.GetProperty("title").GetString();
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
        var title = await HandleAndReadTitleAsync(new KeyNotFoundException("missing"), culture);
        title.ShouldBe(expected);
    }

    [Theory]
    [InlineData("pt-BR", "Ocorreu um erro inesperado")]
    [InlineData("en-US", "An unexpected error occurred")]
    public async Task Unexpected_title_is_localized(string culture, string expected)
    {
        var title = await HandleAndReadTitleAsync(new InvalidOperationException("boom"), culture);
        title.ShouldBe(expected);
    }
}
