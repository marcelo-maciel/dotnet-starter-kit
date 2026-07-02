using FSH.Framework.Web.Cors;
using FSH.Framework.Web.Origin;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FSH.Modules.Identity.Services;

internal sealed class OriginResolver(
    IHttpContextAccessor httpContextAccessor,
    IOptions<CorsOptions> corsOptions,
    IOptions<OriginOptions> originOptions,
    ILogger<OriginResolver> logger) : IOriginResolver
{
    private readonly string[] _allowedOrigins = corsOptions.Value.AllowedOrigins;
    private readonly Uri? _originUrl = originOptions.Value.OriginUrl;

    public string FrontendOrigin()
    {
        var origin = httpContextAccessor.HttpContext?.Request.Headers.Origin.ToString();
        if (!string.IsNullOrWhiteSpace(origin) && IsAllowed(origin))
        {
            return origin.TrimEnd('/');
        }

        // The allow-list check is the security boundary: a forged Origin header on an anonymous
        // request (e.g. forgot-password) must never end up as a link inside an e-mail.
        logger.LogWarning("Rejected frontend origin {Origin}: not present in the CORS allow-list", origin);
        throw new InvalidOperationException(
            "The request origin is not an allowed front-end origin. Configure CorsOptions:AllowedOrigins with the front-end URLs.");
    }

    public string? ApiOrigin()
    {
        if (_originUrl is not null)
        {
            return _originUrl.AbsoluteUri.TrimEnd('/');
        }

        var request = httpContextAccessor.HttpContext?.Request;
        if (request is not null && !string.IsNullOrWhiteSpace(request.Scheme) && request.Host.HasValue)
        {
            return $"{request.Scheme}://{request.Host.Value}{request.PathBase}".TrimEnd('/');
        }

        return null;
    }

    private bool IsAllowed(string origin)
    {
        var normalized = origin.TrimEnd('/');
        foreach (var allowed in _allowedOrigins)
        {
            // Scheme + host are case-insensitive; the port is compared exactly so :5173 never
            // matches :5174. A trailing slash on either side is ignored.
            if (string.Equals(allowed.TrimEnd('/'), normalized, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
