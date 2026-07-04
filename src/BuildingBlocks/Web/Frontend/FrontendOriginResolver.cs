using System.Net;
using FSH.Framework.Core.Exceptions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FSH.Framework.Web.Frontend;

internal sealed class FrontendOriginResolver(
    IHttpContextAccessor httpContextAccessor,
    IOptions<FrontendOptions> options,
    ILogger<FrontendOriginResolver> logger) : IFrontendOriginResolver
{
    // Normalize the allow-list once at construction: parse to Uri so matching is component-wise
    // (scheme + host + port) instead of a raw string compare that an entry like ":443" or an IDN
    // form would silently fail.
    private readonly Uri[] _allowed = Normalize(options.Value.AllowedOrigins);
    private readonly string? _default = options.Value.DefaultOrigin?.TrimEnd('/');

    public string ResolveForCurrentRequest()
    {
        var header = httpContextAccessor.HttpContext?.Request.Headers.Origin.ToString();
        if (string.IsNullOrWhiteSpace(header))
        {
            // Non-browser caller (curl, Scalar try-it, mobile, server-to-server) sends no Origin.
            // Fall back to the configured default rather than failing an otherwise valid flow.
            return ResolveDefault();
        }

        var canonical = MatchAllowed(header);
        if (canonical is not null)
        {
            return canonical;
        }

        // A present-but-unlisted Origin is a forged or misconfigured client, not a server fault:
        // surface a 4xx so error-rate alerting doesn't page on bot traffic to anonymous endpoints.
        logger.LogWarning("Rejected front-end origin {Origin}: not in FrontendOptions:AllowedOrigins", header);
        throw new CustomException(
            "The request origin is not an allowed front-end origin.",
            errors: null,
            HttpStatusCode.BadRequest);
    }

    public string ResolveDefault()
    {
        if (string.IsNullOrEmpty(_default))
        {
            // Startup validation should prevent this; guard anyway so a misconfig surfaces as a
            // clear 500 rather than an empty link silently shipped into an e-mail.
            throw new CustomException(
                "No default front-end origin is configured (FrontendOptions:DefaultOrigin).",
                errors: null,
                HttpStatusCode.InternalServerError);
        }

        return _default;
    }

    private string? MatchAllowed(string header)
    {
        if (!Uri.TryCreate(header.TrimEnd('/'), UriKind.Absolute, out var candidate))
        {
            return null;
        }

        // Return the canonical configured entry, never the client-supplied casing.
        return _allowed
            .FirstOrDefault(allowed => Uri.Compare(
                candidate, allowed, UriComponents.SchemeAndServer, UriFormat.Unescaped, StringComparison.OrdinalIgnoreCase) == 0)
            ?.GetLeftPart(UriPartial.Authority);
    }

    private static Uri[] Normalize(string[] origins)
    {
        var list = new List<Uri>(origins.Length);
        foreach (var origin in origins)
        {
            if (Uri.TryCreate(origin.TrimEnd('/'), UriKind.Absolute, out var uri))
            {
                list.Add(uri);
            }
        }

        return [.. list];
    }
}
