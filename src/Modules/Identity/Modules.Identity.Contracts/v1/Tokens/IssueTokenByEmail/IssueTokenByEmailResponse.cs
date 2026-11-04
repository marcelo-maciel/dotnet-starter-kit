namespace FSH.Modules.Identity.Contracts.v1.Tokens.IssueTokenByEmail;

/// <summary>
/// The kit's token pair plus the tenant it was issued in: every later request and the refresh still carry the
/// <c>tenant</c> header, and the client only learns it here.
/// </summary>
public sealed record IssueTokenByEmailResponse(
    string Tenant,
    string AccessToken,
    string RefreshToken,
    DateTime RefreshTokenExpiresAt,
    DateTime AccessTokenExpiresAt);
