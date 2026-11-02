using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Localization;

namespace FSH.Modules.Identity.Localization;

/// <summary>
/// ASP.NET Core Identity ships its error texts in English only (no satellite resources), and those texts
/// reach the client verbatim in the ProblemDetails <c>errors</c> list (password policy, wrong current
/// password, expired reset token). Each description is resolved from the Identity catalog under the request
/// UI culture; the <see cref="IdentityError.Code"/> stays the base one, so callers branching on it are unaffected.
/// </summary>
public sealed class LocalizedIdentityErrorDescriber(IStringLocalizer<IdentityResources> localizer) : IdentityErrorDescriber
{
    // The base Code is the method name, so it doubles as the catalog key suffix. A missing key keeps the
    // framework's English description instead of surfacing the raw key to the user.
    private IdentityError Localize(IdentityError error, params object[] args)
    {
        var description = localizer["Identity.Error." + error.Code, args];
        return new IdentityError
        {
            Code = error.Code,
            Description = description.ResourceNotFound ? error.Description : description.Value,
        };
    }

    public override IdentityError DefaultError() => Localize(base.DefaultError());

    public override IdentityError ConcurrencyFailure() => Localize(base.ConcurrencyFailure());

    public override IdentityError PasswordMismatch() => Localize(base.PasswordMismatch());

    public override IdentityError InvalidToken() => Localize(base.InvalidToken());

    public override IdentityError RecoveryCodeRedemptionFailed() => Localize(base.RecoveryCodeRedemptionFailed());

    public override IdentityError LoginAlreadyAssociated() => Localize(base.LoginAlreadyAssociated());

    public override IdentityError InvalidUserName(string? userName) => Localize(base.InvalidUserName(userName), userName ?? string.Empty);

    public override IdentityError InvalidEmail(string? email) => Localize(base.InvalidEmail(email), email ?? string.Empty);

    public override IdentityError DuplicateUserName(string userName) => Localize(base.DuplicateUserName(userName), userName);

    public override IdentityError DuplicateEmail(string email) => Localize(base.DuplicateEmail(email), email);

    public override IdentityError InvalidRoleName(string? role) => Localize(base.InvalidRoleName(role), role ?? string.Empty);

    public override IdentityError DuplicateRoleName(string role) => Localize(base.DuplicateRoleName(role), role);

    public override IdentityError UserAlreadyHasPassword() => Localize(base.UserAlreadyHasPassword());

    public override IdentityError UserLockoutNotEnabled() => Localize(base.UserLockoutNotEnabled());

    public override IdentityError UserAlreadyInRole(string role) => Localize(base.UserAlreadyInRole(role), role);

    public override IdentityError UserNotInRole(string role) => Localize(base.UserNotInRole(role), role);

    public override IdentityError PasswordTooShort(int length) => Localize(base.PasswordTooShort(length), length);

    public override IdentityError PasswordRequiresUniqueChars(int uniqueChars) => Localize(base.PasswordRequiresUniqueChars(uniqueChars), uniqueChars);

    public override IdentityError PasswordRequiresNonAlphanumeric() => Localize(base.PasswordRequiresNonAlphanumeric());

    public override IdentityError PasswordRequiresDigit() => Localize(base.PasswordRequiresDigit());

    public override IdentityError PasswordRequiresLower() => Localize(base.PasswordRequiresLower());

    public override IdentityError PasswordRequiresUpper() => Localize(base.PasswordRequiresUpper());
}
