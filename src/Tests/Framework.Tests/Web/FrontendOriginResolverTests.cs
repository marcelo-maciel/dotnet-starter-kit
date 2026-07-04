using System.Net;
using FSH.Framework.Core.Exceptions;
using FSH.Framework.Web.Frontend;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Framework.Tests.Web;

/// <summary>
/// Tests for FrontendOriginResolver — resolves the SPA origin for user-facing links, validating the
/// request Origin header against the allow-list and falling back to the configured default.
/// </summary>
public sealed class FrontendOriginResolverTests
{
    private readonly IHttpContextAccessor _httpContextAccessor = Substitute.For<IHttpContextAccessor>();

    private FrontendOriginResolver CreateResolver(string[] allowedOrigins, string? defaultOrigin = null)
    {
        var options = Options.Create(new FrontendOptions
        {
            AllowedOrigins = allowedOrigins,
            DefaultOrigin = defaultOrigin,
        });
        return new FrontendOriginResolver(_httpContextAccessor, options, NullLogger<FrontendOriginResolver>.Instance);
    }

    private void SetOriginHeader(string? origin)
    {
        var context = new DefaultHttpContext();
        if (origin is not null)
        {
            context.Request.Headers.Origin = origin;
        }

        _httpContextAccessor.HttpContext.Returns(context);
    }

    // ── ResolveForCurrentRequest ────────────────────────────────────────────

    [Fact]
    public void ResolveForCurrentRequest_Should_ReturnCanonicalEntry_When_HeaderInAllowList()
    {
        SetOriginHeader("http://localhost:5173");
        var resolver = CreateResolver(["http://localhost:5173", "http://localhost:5174"]);

        resolver.ResolveForCurrentRequest().ShouldBe("http://localhost:5173");
    }

    [Fact]
    public void ResolveForCurrentRequest_Should_MatchIgnoringTrailingSlash()
    {
        SetOriginHeader("http://localhost:5173/");
        var resolver = CreateResolver(["http://localhost:5173"]);

        resolver.ResolveForCurrentRequest().ShouldBe("http://localhost:5173");
    }

    [Fact]
    public void ResolveForCurrentRequest_Should_ReturnCanonicalCasing_When_HeaderCasingDiffers()
    {
        // A client sending uppercased scheme/host must not steer the emitted link's casing:
        // the resolver returns the canonical allow-list entry, not the raw header.
        SetOriginHeader("HTTP://LOCALHOST:5173");
        var resolver = CreateResolver(["http://localhost:5173"]);

        resolver.ResolveForCurrentRequest().ShouldBe("http://localhost:5173");
    }

    [Fact]
    public void ResolveForCurrentRequest_Should_Reject_When_PortDiffers()
    {
        // :5174 must never match the :5173 allow-list entry (port compared exactly).
        SetOriginHeader("http://localhost:5174");
        var resolver = CreateResolver(["http://localhost:5173"]);

        var ex = Should.Throw<CustomException>(() => resolver.ResolveForCurrentRequest());
        ex.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public void ResolveForCurrentRequest_Should_Reject_When_HeaderForged()
    {
        SetOriginHeader("https://evil.example.com");
        var resolver = CreateResolver(["http://localhost:5173"]);

        var ex = Should.Throw<CustomException>(() => resolver.ResolveForCurrentRequest());
        ex.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public void ResolveForCurrentRequest_Should_FallBackToDefault_When_NoHeader()
    {
        // Non-browser callers (curl, Scalar, mobile, server-to-server) send no Origin — use the default.
        SetOriginHeader(null);
        var resolver = CreateResolver(["http://localhost:5173"], defaultOrigin: "https://app.example.com");

        resolver.ResolveForCurrentRequest().ShouldBe("https://app.example.com");
    }

    [Fact]
    public void ResolveForCurrentRequest_Should_FallBackToDefault_When_NoHttpContext()
    {
        _httpContextAccessor.HttpContext.Returns((HttpContext?)null);
        var resolver = CreateResolver(["http://localhost:5173"], defaultOrigin: "https://app.example.com");

        resolver.ResolveForCurrentRequest().ShouldBe("https://app.example.com");
    }

    // ── ResolveDefault ──────────────────────────────────────────────────────

    [Fact]
    public void ResolveDefault_Should_ReturnConfiguredDefault_TrailingSlashTrimmed()
    {
        var resolver = CreateResolver([], defaultOrigin: "https://app.example.com/");

        resolver.ResolveDefault().ShouldBe("https://app.example.com");
    }

    [Fact]
    public void ResolveDefault_Should_Throw_When_DefaultNotConfigured()
    {
        var resolver = CreateResolver(["http://localhost:5173"], defaultOrigin: null);

        var ex = Should.Throw<CustomException>(() => resolver.ResolveDefault());
        ex.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
    }
}
