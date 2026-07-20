using System.Globalization;
using System.Security.Claims;
using FSH.Framework.Web.Localization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Localization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Framework.Tests.Localization;

public sealed class UserLocaleRequestCultureProviderTests
{
    private static IOptions<RequestLocalizationOptions> BuildOptions()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection().Build();
        var services = new ServiceCollection();
        services.AddHeroLocalization(configuration);
        return services.BuildServiceProvider().GetRequiredService<IOptions<RequestLocalizationOptions>>();
    }

    private static DefaultHttpContext BuildContext(string? claim, string? header, string? query)
    {
        var context = new DefaultHttpContext();
        if (claim is not null)
        {
            context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("locale", claim)], "test"));
        }

        if (header is not null)
        {
            context.Request.Headers.AcceptLanguage = header;
        }

        if (query is not null)
        {
            context.Request.QueryString = new QueryString($"?culture={query}");
        }

        return context;
    }

    // Full culture-provider chain: Query -> user locale claim -> Accept-Language -> configured default -> en-US.
    [Theory]
    [InlineData("pt-BR", "en-US", null, "pt-BR")]    // supported claim wins over the header
    [InlineData(null, "pt-BR", null, "pt-BR")]       // header used when there is no claim
    [InlineData(null, null, null, "en-US")]          // nothing set -> default fallback
    [InlineData("xx-YY", "pt-BR", null, "pt-BR")]    // unsupported claim ignored -> falls to header
    [InlineData(null, "pt-BR", "en-US", "en-US")]    // explicit query override wins over everything
    public async Task Resolves_expected_culture_through_chain(string? claim, string? header, string? query, string expected)
    {
        var options = BuildOptions();
        var context = BuildContext(claim, header, query);

        string? resolved = null;
        var middleware = new RequestLocalizationMiddleware(
            _ => { resolved = CultureInfo.CurrentUICulture.Name; return Task.CompletedTask; },
            options,
            NullLoggerFactory.Instance);

        var previous = (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture);
        try
        {
            await middleware.Invoke(context);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous.Item1;
            CultureInfo.CurrentUICulture = previous.Item2;
        }

        resolved.ShouldBe(expected);
    }

    // The custom provider in isolation: emit the claim only when supported, otherwise fall through (null).
    [Theory]
    [InlineData("pt-BR", "pt-BR")]
    [InlineData("en-US", "en-US")]
    [InlineData("xx-YY", null)]
    [InlineData(null, null)]
    public async Task Provider_returns_claim_only_when_supported(string? claim, string? expected)
    {
        var context = BuildContext(claim, header: null, query: null);
        var result = await new UserLocaleRequestCultureProvider().DetermineProviderCultureResult(context);
        (result?.Cultures[0].Value).ShouldBe(expected);
    }
}
