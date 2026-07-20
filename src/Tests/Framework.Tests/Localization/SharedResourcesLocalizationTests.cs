using System.Globalization;
using FSH.Framework.Core.Localization;
using FSH.Framework.Web.Localization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;

namespace Framework.Tests.Localization;

// Proves the whole resx wiring: SharedResources marker + co-located resx + ResourcesPath="" in
// AddHeroLocalization resolve to the embedded catalog under the current UI culture. If the manifest
// name or ResourcesPath is wrong, ResourceNotFound flips true and IStringLocalizer leaks the raw key.
public sealed class SharedResourcesLocalizationTests
{
    private static IStringLocalizer<SharedResources> BuildLocalizer()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection().Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHeroLocalization(configuration);
        return services.BuildServiceProvider().GetRequiredService<IStringLocalizer<SharedResources>>();
    }

    [Theory]
    [InlineData("pt-BR", "Não encontrado")]   // specific pt-BR falls back to the neutral .pt catalog
    [InlineData("pt", "Não encontrado")]      // neutral pt resolves directly
    [InlineData("pt-PT", "Não encontrado")]   // other pt variant also falls back to .pt
    [InlineData("en-US", "Not Found")]        // en-US falls back to the neutral (default) catalog
    public void Localizer_resolves_error_key_per_culture(string culture, string expected)
    {
        var localizer = BuildLocalizer();
        var previous = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = new CultureInfo(culture);
            var localized = localizer["Error.NotFound"];

            localized.ResourceNotFound.ShouldBeFalse(
                $"resx for '{culture}' did not resolve 'Error.NotFound' — check ResourcesPath/resx manifest name.");
            localized.Value.ShouldBe(expected);
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
        }
    }
}
