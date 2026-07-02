using FSH.Framework.Core.Context;
using FSH.Modules.Identity.Contracts.Services;
using Microsoft.AspNetCore.Http;

namespace FSH.Modules.Identity.Services;

/// <summary>
/// Provides HTTP request context information through an abstraction.
/// This allows handlers to access request metadata without direct ASP.NET Core dependencies.
/// </summary>
internal sealed class RequestContextService : IRequestContextService
{
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly IOriginResolver _originResolver;

    public RequestContextService(
        IHttpContextAccessor httpContextAccessor,
        IOriginResolver originResolver)
    {
        _httpContextAccessor = httpContextAccessor;
        _originResolver = originResolver;
    }

    public string? IpAddress =>
        _httpContextAccessor.HttpContext?.Connection.RemoteIpAddress?.ToString();

    public string? UserAgent =>
        _httpContextAccessor.HttpContext?.Request.Headers.UserAgent.ToString();

    public string ClientId
    {
        get
        {
            var clientId = _httpContextAccessor.HttpContext?.Request.Headers["X-Client-Id"].ToString();
            return string.IsNullOrWhiteSpace(clientId) ? "web" : clientId;
        }
    }

    public string? Origin => _originResolver.ApiOrigin();
}