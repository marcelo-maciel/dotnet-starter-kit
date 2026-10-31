using System.Globalization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Localization;
using Microsoft.Extensions.Options;

namespace Identity.Tests.Support;

/// <summary>
/// The request-localization options AddHeroLocalization builds from LocalizationOptions:DefaultCulture: the
/// UI half of DefaultRequestCulture is the deployment's default language, the formatting half stays invariant.
/// </summary>
internal static class DeploymentCultureOptions
{
    public static IOptions<RequestLocalizationOptions> For(string tag) =>
        Options.Create(new RequestLocalizationOptions
        {
            DefaultRequestCulture = new RequestCulture(CultureInfo.InvariantCulture, CultureInfo.GetCultureInfo(tag)),
        });
}
