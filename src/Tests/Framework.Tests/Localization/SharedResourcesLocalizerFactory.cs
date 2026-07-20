using FSH.Framework.Core.Localization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;

namespace Framework.Tests.Localization;

// Builds a REAL IStringLocalizer<SharedResources> bound to the embedded resx catalog
// (ResourcesPath="" — co-located marker + resx). Shared by the handler and validator tests
// so they exercise the actual catalog resolution rather than a stub.
internal static class SharedResourcesLocalizerFactory
{
    public static IStringLocalizer<SharedResources> Create()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddLocalization(o => o.ResourcesPath = "");
        return services.BuildServiceProvider().GetRequiredService<IStringLocalizer<SharedResources>>();
    }
}
