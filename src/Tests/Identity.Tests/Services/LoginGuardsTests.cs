using FSH.Modules.Identity.Domain;
using FSH.Modules.Identity.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Identity.Tests.Services;

/// <summary>
/// The lockout counter's writes used to be discarded: a failed AccessFailedAsync left lockout silently not counting.
/// A failure is now logged with the opaque user id and the error codes, never the descriptions (they can carry the
/// e-mail).
/// </summary>
public sealed class LoginGuardsTests
{
    private const string EmailNaDescricao = "pessoa@cliente.test";

    private readonly UserManager<FshUser> _userManager = Substitute.For<UserManager<FshUser>>(
        Substitute.For<IUserStore<FshUser>>(), null, null, null, null, null, null, null, null);

    private readonly FshUser _user = new() { Id = "u_123", Email = EmailNaDescricao };

    [Fact]
    public async Task RecordFailedAttempt_Should_LogCodesWithoutPii_When_TheWriteFails()
    {
        _userManager.AccessFailedAsync(_user).Returns(Falha());
        var logger = new CapturingLogger();

        await LoginGuards.RecordFailedAttemptAsync(_userManager, _user, logger);

        var aviso = logger.Entries.ShouldHaveSingleItem();
        aviso.Level.ShouldBe(LogLevel.Warning);
        aviso.Message.ShouldContain("u_123");
        aviso.Message.ShouldContain("ConcurrencyFailure");
        aviso.Message.ShouldNotContain(EmailNaDescricao);
    }

    [Fact]
    public async Task ResetFailedAttempts_Should_LogCodesWithoutPii_When_TheWriteFails()
    {
        _userManager.ResetAccessFailedCountAsync(_user).Returns(Falha());
        var logger = new CapturingLogger();

        await LoginGuards.ResetFailedAttemptsAsync(_userManager, _user, logger);

        logger.Entries.ShouldHaveSingleItem().Message.ShouldNotContain(EmailNaDescricao);
    }

    [Fact]
    public async Task RecordFailedAttempt_Should_LogNothing_When_TheWriteSucceeds()
    {
        _userManager.AccessFailedAsync(_user).Returns(IdentityResult.Success);
        var logger = new CapturingLogger();

        await LoginGuards.RecordFailedAttemptAsync(_userManager, _user, logger);

        logger.Entries.ShouldBeEmpty();
    }

    private static IdentityResult Falha() =>
        IdentityResult.Failed(new IdentityError { Code = "ConcurrencyFailure", Description = $"Optimistic failure for {EmailNaDescricao}" });

    private sealed class CapturingLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            Entries.Add((logLevel, formatter(state, exception)));
        }
    }
}
