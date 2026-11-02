using FSH.Modules.Identity.Localization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;

namespace Identity.Tests.Support;

// Builds a REAL IStringLocalizer<IdentityResources> bound to the embedded Identity resx catalog, so handler and
// service tests read the actual catalog under the ambient UI culture instead of a substituted string.
internal static class IdentityResourcesLocalizerFactory
{
    public static IStringLocalizer<IdentityResources> Create()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddLocalization(o => o.ResourcesPath = "");
        return services.BuildServiceProvider().GetRequiredService<IStringLocalizer<IdentityResources>>();
    }
}
