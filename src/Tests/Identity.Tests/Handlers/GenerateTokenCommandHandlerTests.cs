using AutoFixture;
using Finbuckle.MultiTenant.Abstractions;
using FSH.Framework.Core.Context;
using FSH.Framework.Eventing.Outbox;
using FSH.Framework.Shared.Multitenancy;
using FSH.Modules.Auditing.Contracts;
using FSH.Modules.Identity.Contracts.DTOs;
using FSH.Modules.Identity.Contracts.Services;
using FSH.Modules.Identity.Contracts.v1.Tokens.TokenGeneration;
using FSH.Modules.Identity.Features.v1.Tokens.TokenGeneration;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;

namespace Identity.Tests.Handlers;

/// <summary>
/// Tests for GenerateTokenCommandHandler - handles user login and token generation.
/// </summary>
public sealed class GenerateTokenCommandHandlerTests
{
    private readonly IIdentityService _identityService;
    private readonly ITokenService _tokenService;
    private readonly ISecurityAudit _securityAudit;
    private readonly IRequestContext _requestContext;
    private readonly IOutboxStore _outboxStore;
    private readonly IMultiTenantContextAccessor<AppTenantInfo> _multiTenantContextAccessor;
    private readonly ISessionService _sessionService;
    private readonly ILogger<GenerateTokenCommandHandler> _logger;
    private readonly GenerateTokenCommandHandler _sut;
    private readonly IFixture _fixture;
    private readonly Guid _sessionId = Guid.NewGuid();

    public GenerateTokenCommandHandlerTests()
    {
        _identityService = Substitute.For<IIdentityService>();
        _tokenService = Substitute.For<ITokenService>();
        _securityAudit = Substitute.For<ISecurityAudit>();
        _requestContext = Substitute.For<IRequestContext>();
        _outboxStore = Substitute.For<IOutboxStore>();
        _multiTenantContextAccessor = Substitute.For<IMultiTenantContextAccessor<AppTenantInfo>>();
        _sessionService = Substitute.For<ISessionService>();
        _logger = Substitute.For<ILogger<GenerateTokenCommandHandler>>();

        _sut = new GenerateTokenCommandHandler(
            _identityService,
            _tokenService,
            _securityAudit,
            _requestContext,
            _outboxStore,
            _multiTenantContextAccessor,
            _sessionService,
            _logger);

        _fixture = new Fixture();

        // PAF: login now creates the session before signing, so every successful login needs one.
        _sessionService.CreateSessionAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(new UserSessionDto { Id = _sessionId });
    }

    // The handler signs the validated claims plus the session id (sid), never the bare claims.
    private IEnumerable<Claim> ClaimsWithSession(List<Claim> claims) =>
        Arg.Is<IEnumerable<Claim>>(issued =>
            issued.Count() == claims.Count + 1
            && issued.Take(claims.Count).SequenceEqual(claims)
            && issued.Last().Type == JwtRegisteredClaimNames.Sid
            && issued.Last().Value == _sessionId.ToString());

    #region Handle - Happy Path Tests

    [Fact]
    public async Task Handle_Should_ReturnTokenResponse_When_CredentialsAreValid()
    {
        // Arrange
        var command = new GenerateTokenCommand("user@example.com", "password123");
        var userId = _fixture.Create<string>();
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, userId),
            new(ClaimTypes.Name, "TestUser"),
            new(ClaimTypes.Email, command.Email)
        };
        var expectedToken = new TokenResponse(
            AccessToken: _fixture.Create<string>(),
            RefreshToken: _fixture.Create<string>(),
            RefreshTokenExpiresAt: DateTime.UtcNow.AddDays(7),
            AccessTokenExpiresAt: DateTime.UtcNow.AddHours(1));

        _requestContext.IpAddress.Returns("192.168.1.1");
        _requestContext.UserAgent.Returns("TestAgent");
        _requestContext.ClientId.Returns("test-client");

        _identityService.ValidateCredentialsAsync(command.Email, command.Password, Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns((userId, claims));

        _tokenService.IssueAsync(userId, ClaimsWithSession(claims), null, Arg.Any<CancellationToken>())
            .Returns(expectedToken);

        // Act
        var result = await _sut.Handle(command, CancellationToken.None);

        // Assert
        result.ShouldNotBeNull();
        result.AccessToken.ShouldBe(expectedToken.AccessToken);
        result.RefreshToken.ShouldBe(expectedToken.RefreshToken);
        result.RefreshTokenExpiresAt.ShouldBe(expectedToken.RefreshTokenExpiresAt);
        result.AccessTokenExpiresAt.ShouldBe(expectedToken.AccessTokenExpiresAt);
    }

    [Fact]
    public async Task Handle_Should_CallAllServicesWithCorrectParameters_When_CredentialsAreValid()
    {
        // Arrange
        var command = new GenerateTokenCommand("user@example.com", "password123");
        var userId = _fixture.Create<string>();
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, userId),
            new(ClaimTypes.Name, "TestUser")
        };
        var token = _fixture.Create<TokenResponse>();

        _requestContext.IpAddress.Returns("192.168.1.1");
        _requestContext.UserAgent.Returns("TestAgent");
        _requestContext.ClientId.Returns("test-client");

        _identityService.ValidateCredentialsAsync(command.Email, command.Password, Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns((userId, claims));

        _tokenService.IssueAsync(userId, ClaimsWithSession(claims), null, Arg.Any<CancellationToken>())
            .Returns(token);

        // Act
        await _sut.Handle(command, CancellationToken.None);

        // Assert
        await _identityService.Received(1).ValidateCredentialsAsync(command.Email, command.Password, Arg.Any<string?>(), Arg.Any<CancellationToken>());
        await _tokenService.Received(1).IssueAsync(userId, ClaimsWithSession(claims), null, Arg.Any<CancellationToken>());
        await _identityService.Received(1).StoreRefreshTokenAsync(userId, token.RefreshToken, token.RefreshTokenExpiresAt, Arg.Any<CancellationToken>());
        await _securityAudit.Received(1).LoginSucceededAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _securityAudit.Received(1).TokenIssuedAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
        await _outboxStore.Received(1).AddAsync(Arg.Any<FSH.Framework.Eventing.Abstractions.IIntegrationEvent>(), Arg.Any<CancellationToken>());
    }

    #endregion

    #region Handle - Invalid Credentials Tests

    [Fact]
    public async Task Handle_Should_ThrowUnauthorizedAccessException_When_CredentialsAreInvalid()
    {
        // Arrange
        var command = new GenerateTokenCommand("user@example.com", "wrongpassword");

        _requestContext.IpAddress.Returns("192.168.1.1");
        _requestContext.ClientId.Returns("test-client");

        _identityService.ValidateCredentialsAsync(command.Email, command.Password, Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns((ValueTuple<string, IReadOnlyList<Claim>>?)null);

        // Act & Assert
        var exception = await Should.ThrowAsync<UnauthorizedAccessException>(
            async () => await _sut.Handle(command, CancellationToken.None));

        exception.Message.ShouldBe("Invalid credentials.");
    }

    [Fact]
    public async Task Handle_Should_AuditFailedLogin_When_CredentialsAreInvalid()
    {
        // Arrange
        var command = new GenerateTokenCommand("user@example.com", "wrongpassword");

        _requestContext.IpAddress.Returns("192.168.1.1");
        _requestContext.ClientId.Returns("test-client");

        _identityService.ValidateCredentialsAsync(command.Email, command.Password, Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns((ValueTuple<string, IReadOnlyList<Claim>>?)null);

        // Act
        await Should.ThrowAsync<UnauthorizedAccessException>(
            async () => await _sut.Handle(command, CancellationToken.None));

        // Assert
        await _securityAudit.Received(1).LoginFailedAsync(
            command.Email,
            "test-client",
            "InvalidCredentials",
            "192.168.1.1",
            Arg.Any<CancellationToken>());
    }

    #endregion

    #region Handle - Null Command Tests

    [Fact]
    public async Task Handle_Should_ThrowArgumentNullException_When_CommandIsNull()
    {
        // Act & Assert
        await Should.ThrowAsync<ArgumentNullException>(async () =>
            await _sut.Handle(null!, CancellationToken.None));
    }

    #endregion

    #region Handle - CancellationToken Tests

    [Fact]
    public async Task Handle_Should_PassCancellationToken_ToAllServices()
    {
        // Arrange
        var command = new GenerateTokenCommand("user@example.com", "password123");
        var userId = _fixture.Create<string>();
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, userId) };
        var token = _fixture.Create<TokenResponse>();
        using var cts = new CancellationTokenSource();
        var cancellationToken = cts.Token;

        _requestContext.IpAddress.Returns("192.168.1.1");
        _requestContext.UserAgent.Returns("TestAgent");
        _requestContext.ClientId.Returns("test-client");

        _identityService.ValidateCredentialsAsync(command.Email, command.Password, null, cancellationToken)
            .Returns((userId, claims));

        _tokenService.IssueAsync(userId, ClaimsWithSession(claims), null, cancellationToken)
            .Returns(token);

        // Act
        await _sut.Handle(command, cancellationToken);

        // Assert
        await _identityService.Received(1).ValidateCredentialsAsync(command.Email, command.Password, null, cancellationToken);
        await _tokenService.Received(1).IssueAsync(userId, ClaimsWithSession(claims), null, cancellationToken);
        await _identityService.Received(1).StoreRefreshTokenAsync(userId, token.RefreshToken, token.RefreshTokenExpiresAt, cancellationToken);
        await _sessionService.Received(1).CreateSessionAsync(userId, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DateTime>(), cancellationToken);
        await _outboxStore.Received(1).AddAsync(Arg.Any<FSH.Framework.Eventing.Abstractions.IIntegrationEvent>(), cancellationToken);
    }

    #endregion

    #region Handle - Session Binding Tests

    [Fact]
    public async Task Handle_Should_SignSessionIdClaim_And_RekeySessionToIssuedRefreshToken()
    {
        // Arrange
        var command = new GenerateTokenCommand("user@example.com", "password123");
        var userId = _fixture.Create<string>();
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, userId) };
        var token = _fixture.Create<TokenResponse>();
        IEnumerable<Claim>? signedClaims = null;
        string? provisionalHash = null;

        _requestContext.IpAddress.Returns("192.168.1.1");
        _requestContext.UserAgent.Returns("TestAgent");

        _identityService.ValidateCredentialsAsync(command.Email, command.Password, Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns((userId, claims));

        _sessionService.CreateSessionAsync(userId, Arg.Do<string>(h => provisionalHash = h), "192.168.1.1", "TestAgent", Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(new UserSessionDto { Id = _sessionId });

        _tokenService.IssueAsync(userId, Arg.Do<IEnumerable<Claim>>(c => signedClaims = c), null, Arg.Any<CancellationToken>())
            .Returns(token);

        // Act
        await _sut.Handle(command, CancellationToken.None);

        // Assert
        signedClaims.ShouldNotBeNull();
        signedClaims.Single(c => c.Type == JwtRegisteredClaimNames.Sid).Value.ShouldBe(_sessionId.ToString());
        provisionalHash.ShouldNotBeNull();
        provisionalHash.ShouldStartWith("pending-");
        await _sessionService.Received(1).UpdateSessionRefreshTokenAsync(
            provisionalHash, Arg.Is<string>(h => h != provisionalHash), token.RefreshTokenExpiresAt, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_Should_RefuseLogin_When_SessionCreationFails()
    {
        // Arrange
        var command = new GenerateTokenCommand("user@example.com", "password123");
        var userId = _fixture.Create<string>();
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, userId) };

        _requestContext.IpAddress.Returns("192.168.1.1");
        _requestContext.UserAgent.Returns("TestAgent");
        _requestContext.ClientId.Returns("test-client");

        _identityService.ValidateCredentialsAsync(command.Email, command.Password, Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns((userId, claims));

        _sessionService.CreateSessionAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Database not available"));

        // Act
        var exception = await Should.ThrowAsync<InvalidOperationException>(
            async () => await _sut.Handle(command, CancellationToken.None));

        // Assert: no token is signed, stored, audited or announced without a session behind it
        exception.Message.ShouldBe("Database not available");
        await _tokenService.DidNotReceiveWithAnyArgs().IssueAsync(default!, default!, default, default);
        await _identityService.DidNotReceiveWithAnyArgs().StoreRefreshTokenAsync(default!, default!, default, default);
        await _securityAudit.DidNotReceiveWithAnyArgs().TokenIssuedAsync(default!, default!, default!, default!, default, default);
        await _outboxStore.DidNotReceiveWithAnyArgs().AddAsync(default!, default);
    }

    #endregion

    #region Handle - Request Context Tests

    [Fact]
    public async Task Handle_Should_HandleMissingRequestContextValues()
    {
        // Arrange
        var command = new GenerateTokenCommand("user@example.com", "password123");
        var userId = _fixture.Create<string>();
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, userId) };
        var token = _fixture.Create<TokenResponse>();

        // Request context returns null values
        _requestContext.IpAddress.Returns((string?)null);
        _requestContext.UserAgent.Returns((string?)null);
        _requestContext.ClientId.Returns("test-client");

        _identityService.ValidateCredentialsAsync(command.Email, command.Password, Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns((userId, claims));

        _tokenService.IssueAsync(userId, ClaimsWithSession(claims), null, Arg.Any<CancellationToken>())
            .Returns(token);

        // Act
        var result = await _sut.Handle(command, CancellationToken.None);

        // Assert
        result.ShouldNotBeNull();
        await _securityAudit.Received().LoginSucceededAsync(
            userId,
            Arg.Any<string>(),
            "test-client",
            "unknown", // IP should default to "unknown"
            "unknown", // UserAgent should default to "unknown"
            Arg.Any<CancellationToken>());
    }

    #endregion
}