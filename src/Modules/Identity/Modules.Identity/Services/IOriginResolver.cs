namespace FSH.Modules.Identity.Services;

/// <summary>
/// Resolves the base URL used to build user-facing links, distinguishing links that land on a
/// front-end single-page app from links and assets served by the API itself.
/// </summary>
public interface IOriginResolver
{
    /// <summary>
    /// Origin of the calling single-page app, taken from the request <c>Origin</c> header and
    /// validated against the CORS allow-list. Used for links that land on a front-end page
    /// (password reset, e-mail confirmation). Throws when the request carries no allow-listed origin.
    /// </summary>
    string FrontendOrigin();

    /// <summary>
    /// Origin of the API itself, used for links and assets served by the back-end (avatars, API routes).
    /// Prefers the configured origin, falling back to the request host. Null when neither is available.
    /// </summary>
    string? ApiOrigin();
}
