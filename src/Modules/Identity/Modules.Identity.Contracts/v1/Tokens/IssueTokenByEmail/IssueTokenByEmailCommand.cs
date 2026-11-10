using Mediator;

namespace FSH.Modules.Identity.Contracts.v1.Tokens.IssueTokenByEmail;

/// <summary>Client login with no tenant: the server finds the one client tenant that holds the e-mail.</summary>
public sealed record IssueTokenByEmailCommand(
    string Email,
    string Password,
    string? TwoFactorCode)
    : ICommand<IssueTokenByEmailResponse>;
