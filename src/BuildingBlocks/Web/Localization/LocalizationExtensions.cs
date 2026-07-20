using FSH.Framework.Core.Localization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Localization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FSH.Framework.Web.Localization;

public static class LocalizationExtensions
{
    /// <summary>
    /// Registers request localization: resx-backed <c>IStringLocalizer</c> and a culture-provider
    /// chain of Query → user <c>locale</c> claim → Accept-Language → configured default → en-US.
    /// The per-deployment default is read from <c>LocalizationOptions:DefaultCulture</c> and validated
    /// against the whitelist, so garbage config falls back to the guaranteed neutral culture.
    /// </summary>
    public static IServiceCollection AddHeroLocalization(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var configured = configuration["LocalizationOptions:DefaultCulture"];
        var defaultCulture = SupportedCultures.Tags.Contains(configured!) ? configured! : SupportedCultures.Default;

        // ResourcesPath = "" because SharedResources and its resx live in the same folder/namespace
        // (co-located). A non-empty path would double the prefix and IStringLocalizer would silently
        // fall back to the raw key. The resx-resolution test guards this value.
        services.AddLocalization(o => o.ResourcesPath = "");

        services.Configure<RequestLocalizationOptions>(o =>
        {
            o.SetDefaultCulture(defaultCulture);
            o.AddSupportedCultures(SupportedCultures.RequestMatch);
            o.AddSupportedUICultures(SupportedCultures.RequestMatch);
            o.ApplyCurrentCultureToResponseHeaders = true;

            // Default order is [Query(0), Cookie(1), AcceptLanguage(2)]. Drop the cookie provider by
            // type (order-independent, so a framework reshuffle of the defaults can't silently remove
            // the wrong provider) and insert the user-claim provider right after query, so the final
            // chain is Query → UserLocaleClaim → AcceptLanguage → configured default → en-US neutral resx.
            var cookieProvider = o.RequestCultureProviders
                .FirstOrDefault(p => p is CookieRequestCultureProvider);
            if (cookieProvider is not null)
            {
                o.RequestCultureProviders.Remove(cookieProvider);
            }

            o.RequestCultureProviders.Insert(1, new UserLocaleRequestCultureProvider());
        });

        return services;
    }

    public static IApplicationBuilder UseHeroLocalization(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.UseRequestLocalization();
    }
}
