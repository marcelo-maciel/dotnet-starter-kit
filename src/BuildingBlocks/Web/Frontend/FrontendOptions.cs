namespace FSH.Framework.Web.Frontend;

/// <summary>
/// Configuration for resolving the front-end (SPA) origin used when building user-facing links
/// inside e-mails and notifications. Deliberately separate from <c>CorsOptions</c>: the CORS
/// allow-list governs which browsers may call the API, while this list governs which origins may
/// be embedded in an outbound link. The two often overlap but carry different security duties, and
/// coupling them breaks same-origin/reverse-proxy topologies where CORS needs no entries yet links
/// still must resolve.
/// </summary>
public sealed class FrontendOptions
{
    /// <summary>
    /// Origins trusted to appear in user-facing links. A request's <c>Origin</c> header is only
    /// echoed into a link when it matches an entry here (scheme + host + port, port exact). Empty is
    /// valid only when <see cref="DefaultOrigin"/> is set, in which case every link uses the default.
    /// </summary>
    public string[] AllowedOrigins { get; init; } = [];

    /// <summary>
    /// Front-end origin used when the request carries no usable <c>Origin</c> header (non-browser
    /// callers such as curl / the Scalar try-it UI / mobile apps / server-to-server), for
    /// operator-driven flows whose link must land on the recipient's app rather than the caller's,
    /// and for background jobs that run without an HTTP request. Typically the tenant dashboard URL.
    /// </summary>
    public string? DefaultOrigin { get; init; }
}
