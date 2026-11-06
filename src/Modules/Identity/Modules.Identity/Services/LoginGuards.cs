using FSH.Modules.Identity.Domain;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;

namespace FSH.Modules.Identity.Services;

/// <summary>
/// The pieces both logins (token/issue and token/issue-by-email) share so that no failure tells one account state
/// from another: a decoy hash where there is no password to check, and the lockout counter with its results read.
/// </summary>
internal static class LoginGuards
{
    private static string? _decoyHash;

    /// <summary>
    /// One real hash verification for a path that has no account to check (unknown e-mail, look-alike address,
    /// locked account), so the answer costs what a wrong password costs.
    /// </summary>
    public static void VerifyDecoy(IPasswordHasher<FshUser> hasher, string password)
    {
        ArgumentNullException.ThrowIfNull(hasher);
        var decoy = LazyInitializer.EnsureInitialized(
            ref _decoyHash,
            () => hasher.HashPassword(new FshUser(), Guid.NewGuid().ToString("N")));
        _ = hasher.VerifyHashedPassword(new FshUser(), decoy, password);
    }

    /// <summary>Counts a wrong password; a write that fails is logged, since lockout then silently stops counting.</summary>
    public static async Task RecordFailedAttemptAsync(UserManager<FshUser> userManager, FshUser user, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(userManager);
        ArgumentNullException.ThrowIfNull(user);
        WarnIfFailed(await userManager.AccessFailedAsync(user).ConfigureAwait(false), "AccessFailed", user.Id, logger);
    }

    public static async Task ResetFailedAttemptsAsync(UserManager<FshUser> userManager, FshUser user, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(userManager);
        ArgumentNullException.ThrowIfNull(user);
        WarnIfFailed(await userManager.ResetAccessFailedCountAsync(user).ConfigureAwait(false), "ResetAccessFailedCount", user.Id, logger);
    }

    // Error codes only: an IdentityError description can carry the e-mail (DuplicateEmail), which logs never hold.
    private static void WarnIfFailed(IdentityResult result, string operation, string userId, ILogger logger)
    {
        if (result.Succeeded || !logger.IsEnabled(LogLevel.Warning))
        {
            return;
        }

        logger.LogWarning(
            "{Operation} did not persist for user {UserId}: {ErrorCodes}",
            operation,
            userId,
            string.Join(",", result.Errors.Select(e => e.Code)));
    }
}
