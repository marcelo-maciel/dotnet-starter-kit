using AutoFixture;
using FSH.Modules.Identity.Contracts.Services;
using FSH.Modules.Identity.Contracts.v1.Users.ResetPassword;
using FSH.Modules.Identity.Features.v1.Users.ResetPassword;
using Identity.Tests.Support;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Identity.Tests.Handlers;

public sealed class ResetPasswordCommandHandlerTests
{
    private readonly IUserService _userService;
    private readonly ResetPasswordCommandHandler _sut;
    private readonly IFixture _fixture;

    public ResetPasswordCommandHandlerTests()
    {
        _userService = Substitute.For<IUserService>();
        _sut = new ResetPasswordCommandHandler(_userService, IdentityResourcesLocalizerFactory.Create());
        _fixture = new Fixture();
    }

    [Theory]
    [InlineData("en-US", "Password has been reset.")]
    [InlineData("pt-BR", "A senha foi redefinida.")]
    public async Task Handle_Should_ReturnConfirmationInTheRequestCulture(string culture, string expected)
    {
        // Arrange
        var command = _fixture.Create<ResetPasswordCommand>();
        using var scope = new UiCultureScope(culture);

        // Act
        var result = await _sut.Handle(command, CancellationToken.None);

        // Assert
        result.ShouldBe(expected);
    }

    [Fact]
    public async Task Handle_Should_CallResetPasswordAsync_WithCorrectParameters()
    {
        // Arrange
        var command = _fixture.Create<ResetPasswordCommand>();

        // Act
        using var culture = new UiCultureScope("en-US");
        var result = await _sut.Handle(command, CancellationToken.None);

        // Assert
        result.ShouldBe("Password has been reset.");
        await _userService.Received(1).ResetPasswordAsync(command.Email, command.Password, command.Token, Arg.Any<CancellationToken>());
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
        var command = _fixture.Create<ResetPasswordCommand>();
        using var cts = new CancellationTokenSource();

        // Act
        await _sut.Handle(command, cts.Token);

        // Assert
        await _userService.Received(1).ResetPasswordAsync(command.Email, command.Password, command.Token, cts.Token);
    }
}
