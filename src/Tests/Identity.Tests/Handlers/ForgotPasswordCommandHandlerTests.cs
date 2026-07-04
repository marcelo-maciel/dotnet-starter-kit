using AutoFixture;
using FSH.Framework.Web.Frontend;
using FSH.Modules.Identity.Contracts.Services;
using FSH.Modules.Identity.Contracts.v1.Users.ForgotPassword;
using FSH.Modules.Identity.Features.v1.Users.ForgotPassword;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Identity.Tests.Handlers;

public sealed class ForgotPasswordCommandHandlerTests
{
    private readonly IUserService _userService;
    private readonly IFrontendOriginResolver _originResolver;
    private readonly ForgotPasswordCommandHandler _sut;
    private readonly IFixture _fixture;

    public ForgotPasswordCommandHandlerTests()
    {
        _userService = Substitute.For<IUserService>();
        _originResolver = Substitute.For<IFrontendOriginResolver>();
        _sut = new ForgotPasswordCommandHandler(_userService, _originResolver);
        _fixture = new Fixture();
    }

    [Fact]
    public async Task Handle_Should_CallForgotPasswordAsync_With_ResolvedFrontendOrigin()
    {
        // Arrange
        var command = _fixture.Create<ForgotPasswordCommand>();
        const string origin = "https://app.example.com";
        _originResolver.ResolveForCurrentRequest().Returns(origin);

        // Act
        var result = await _sut.Handle(command, CancellationToken.None);

        // Assert
        result.ShouldBe("Password reset email sent.");
        await _userService.Received(1).ForgotPasswordAsync(command.Email, origin, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_Should_Propagate_When_OriginResolverThrows()
    {
        // Arrange - a request with a forged Origin header cannot build a reset link.
        var command = _fixture.Create<ForgotPasswordCommand>();
        _originResolver.ResolveForCurrentRequest().Returns(_ => throw new InvalidOperationException("no origin"));

        // Act & Assert
        await Should.ThrowAsync<InvalidOperationException>(async () =>
            await _sut.Handle(command, CancellationToken.None));
        await _userService.DidNotReceive().ForgotPasswordAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_Should_ThrowArgumentNullException_When_CommandIsNull()
    {
        // Act & Assert
        await Should.ThrowAsync<ArgumentNullException>(async () =>
            await _sut.Handle(null!, CancellationToken.None));
    }

    [Fact]
    public async Task Handle_Should_PassCancellationToken_ToUserService()
    {
        // Arrange
        var command = _fixture.Create<ForgotPasswordCommand>();
        _originResolver.ResolveForCurrentRequest().Returns("https://app.example.com");
        using var cts = new CancellationTokenSource();

        // Act
        await _sut.Handle(command, cts.Token);

        // Assert
        await _userService.Received(1).ForgotPasswordAsync(command.Email, Arg.Any<string>(), cts.Token);
    }
}
