using System.Globalization;
using System.Linq;
using FSH.Modules.Catalog.Localization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;

namespace Catalog.Tests.Localization;

// Proves the CatalogResources catalog is embedded under the correct manifest name (ResourcesPath="" =>
// co-located marker + resx). A wrong manifest name flips ResourceNotFound and leaks raw keys; a
// missing pt entry ships English as "translated". Both are caught here.
public sealed class CatalogResourcesTests
{
    private static IStringLocalizer BuildLocalizer()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddLocalization(o => o.ResourcesPath = "");
        return services.BuildServiceProvider()
            .GetRequiredService<IStringLocalizerFactory>()
            .Create(typeof(CatalogResources));
    }

    private static List<string> KeysFor(string culture)
    {
        var localizer = BuildLocalizer();
        var previous = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = culture.Length == 0
                ? CultureInfo.InvariantCulture
                : new CultureInfo(culture);
            return localizer.GetAllStrings(includeParentCultures: false)
                .Select(s => s.Name)
                .ToList();
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
        }
    }

    [Fact]
    public void Neutral_and_pt_catalogs_have_matching_keys()
    {
        var neutral = KeysFor(string.Empty);   // CatalogResources.resx (English / fallback)
        var pt = KeysFor("pt");                 // CatalogResources.pt.resx

        neutral.ShouldNotBeEmpty();
        pt.OrderBy(k => k, StringComparer.Ordinal)
            .ShouldBe(neutral.OrderBy(k => k, StringComparer.Ordinal));
    }

    [Fact]
    public void Known_key_resolves_and_differs_between_en_and_pt()
    {
        var localizer = BuildLocalizer();
        var previous = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = new CultureInfo("en-US");
            var en = localizer["Catalog.CategoryCannotBeOwnParent"];
            en.ResourceNotFound.ShouldBeFalse(
                "resx did not resolve 'Catalog.CategoryCannotBeOwnParent' for en-US — check ResourcesPath/resx manifest name.");
            en.Value.ShouldBe("A category cannot be its own parent.");

            CultureInfo.CurrentUICulture = new CultureInfo("pt-BR");
            var pt = localizer["Catalog.CategoryCannotBeOwnParent"];
            pt.ResourceNotFound.ShouldBeFalse(
                "resx did not resolve 'Catalog.CategoryCannotBeOwnParent' for pt-BR — check the .pt catalog manifest name.");
            pt.Value.ShouldBe("Uma categoria não pode ser pai de si mesma.");

            pt.Value.ShouldNotBe(en.Value);
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
        }
    }
}
