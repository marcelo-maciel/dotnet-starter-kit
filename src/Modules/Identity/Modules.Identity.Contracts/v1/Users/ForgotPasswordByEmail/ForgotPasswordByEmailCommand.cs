using Mediator;

namespace FSH.Modules.Identity.Contracts.v1.Users.ForgotPasswordByEmail;

/// <summary>Password reset request with no tenant: the reset link the e-mail carries names it.</summary>
public sealed record ForgotPasswordByEmailCommand(string Email) : ICommand<string>;
