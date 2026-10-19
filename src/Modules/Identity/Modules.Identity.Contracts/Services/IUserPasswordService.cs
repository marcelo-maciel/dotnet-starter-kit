namespace FSH.Modules.Identity.Contracts.Services;

/// <summary>
/// Service for user password operations.
/// </summary>
public interface IUserPasswordService
{
    /// <summary>
    /// Initiates the forgot password flow by sending a reset email.
    /// </summary>
    Task ForgotPasswordAsync(string email, string origin, CancellationToken cancellationToken);

    /// <summary>
    /// Resets a user's password using a token.
    /// </summary>
    Task ResetPasswordAsync(string email, string password, string token, CancellationToken cancellationToken);

    /// <summary>
    /// Changes the current user's password.
    /// </summary>
    Task ChangePasswordAsync(string password, string newPassword, string confirmNewPassword, string userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets another user's password on an administrator's behalf, without the current password or an e-mailed token,
    /// under the same password policy and history as a change. The caller authorizes the actor; the method does not.
    /// </summary>
    Task SetPasswordByAdminAsync(string userId, string newPassword, CancellationToken cancellationToken);
}