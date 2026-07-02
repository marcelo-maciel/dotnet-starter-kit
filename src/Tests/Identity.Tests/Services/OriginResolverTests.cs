using FSH.Framework.Web.Cors;
using FSH.Framework.Web.Origin;
using FSH.Modules.Identity.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Identity.Tests.Services;

/// <summary>
/// Tests for OriginResolver - resolves the front-end origin (Origin header validated against the CORS
/// allow-list) and the API origin (configured, else request-derived).
/// </summary>
public sealed class OriginResolverTests
{
    private readonly IHttpContextAccessor _httpContextAccessor = Substitute.For<IHttpContextAccessor>();

    private OriginResolver CreateResolver(string[] allowedOrigins, Uri? originUrl = null)
    {
        var cors = Options.Create(new CorsOptions { AllowedOrigins = allowedOrigins });
        var origin = Options.Create(new OriginOptions { OriginUrl = originUrl });
        return new OriginResolver(_httpContextAccessor, cors, origin, NullLogger<OriginResolver>.Instance);
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

    #region FrontendOrigin

    [Fact]
    public void FrontendOrigin_Should_ReturnOrigin_When_HeaderInAllowList()
    {
        // Arrange
        SetOriginHeader("http://localhost:5173");
        var resolver = CreateResolver(["http://localhost:5173", "http://localhost:5174"]);

        // Act
        var result = resolver.FrontendOrigin();

        // Assert
        result.ShouldBe("http://localhost:5173");
    }

    [Fact]
    public void FrontendOrigin_Should_MatchIgnoringTrailingSlash()
    {
        // Arrange - header has a trailing slash, allow-list entry does not
        SetOriginHeader("http://localhost:5173/");
        var resolver = CreateResolver(["http://localhost:5173"]);

        // Act
        var result = resolver.FrontendOrigin();

        // Assert
        result.ShouldBe("http://localhost:5173");
    }

    [Fact]
    public void FrontendOrigin_Should_MatchIgnoringCase()
    {
        // Arrange
        SetOriginHeader("HTTP://LOCALHOST:5173");
        var resolver = CreateResolver(["http://localhost:5173"]);

        // Act
        var result = resolver.FrontendOrigin();

        // Assert
        result.ShouldBe("HTTP://LOCALHOST:5173");
    }

    [Fact]
    public void FrontendOrigin_Should_Throw_When_PortDiffers()
    {
        // Arrange - :5174 must never match the :5173 allow-list entry
        SetOriginHeader("http://localhost:5174");
        var resolver = CreateResolver(["http://localhost:5173"]);

        // Act & Assert
        Should.Throw<InvalidOperationException>(() => resolver.FrontendOrigin());
    }

    [Fact]
    public void FrontendOrigin_Should_Throw_When_HeaderNotInAllowList()
    {
        // Arrange - a forged Origin header must be rejected
        SetOriginHeader("https://evil.example.com");
        var resolver = CreateResolver(["http://localhost:5173"]);

        // Act & Assert
        Should.Throw<InvalidOperationException>(() => resolver.FrontendOrigin());
    }

    [Fact]
    public void FrontendOrigin_Should_Throw_When_NoHeader()
    {
        // Arrange
        SetOriginHeader(null);
        var resolver = CreateResolver(["http://localhost:5173"]);

        // Act & Assert
        Should.Throw<InvalidOperationException>(() => resolver.FrontendOrigin());
    }

    [Fact]
    public void FrontendOrigin_Should_Throw_When_NoHttpContext()
    {
        // Arrange
        _httpContextAccessor.HttpContext.Returns((HttpContext?)null);
        var resolver = CreateResolver(["http://localhost:5173"]);

        // Act & Assert
        Should.Throw<InvalidOperationException>(() => resolver.FrontendOrigin());
    }

    [Fact]
    public void FrontendOrigin_Should_Throw_When_AllowListEmpty_EvenWithHeader()
    {
        // Arrange - AllowAll defaults to true, but an empty allow-list must not trust any origin for links
        SetOriginHeader("http://localhost:5173");
        var resolver = CreateResolver([]);

        // Act & Assert
        Should.Throw<InvalidOperationException>(() => resolver.FrontendOrigin());
    }

    #endregion

    #region ApiOrigin

    [Fact]
    public void ApiOrigin_Should_ReturnConfigured_When_OriginUrlSet()
    {
        // Arrange - configured origin wins and is trailing-slash trimmed
        var context = new DefaultHttpContext();
        context.Request.Scheme = "http";
        context.Request.Host = new HostString("request.example.com");
        _httpContextAccessor.HttpContext.Returns(context);
        var resolver = CreateResolver([], new Uri("https://configured.example.com/"));

        // Act
        var result = resolver.ApiOrigin();

        // Assert
        result.ShouldBe("https://configured.example.com");
    }

    [Fact]
    public void ApiOrigin_Should_DeriveFromRequest_When_OriginUrlNull()
    {
        // Arrange
        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("api.example.com");
        context.Request.PathBase = new PathString("/base");
        _httpContextAccessor.HttpContext.Returns(context);
        var resolver = CreateResolver([], originUrl: null);

        // Act
        var result = resolver.ApiOrigin();

        // Assert
        result.ShouldBe("https://api.example.com/base");
    }

    [Fact]
    public void ApiOrigin_Should_ReturnNull_When_OriginUrlNullAndNoHttpContext()
    {
        // Arrange
        _httpContextAccessor.HttpContext.Returns((HttpContext?)null);
        var resolver = CreateResolver([], originUrl: null);

        // Act
        var result = resolver.ApiOrigin();

        // Assert
        result.ShouldBeNull();
    }

    #endregion
}
