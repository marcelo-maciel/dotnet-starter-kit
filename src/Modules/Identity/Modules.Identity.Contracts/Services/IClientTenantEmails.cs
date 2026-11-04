namespace FSH.Modules.Identity.Contracts.Services;

/// <summary>
/// The PAF signs client users in by e-mail alone, so an e-mail names one person across every client tenant
/// (O1). Root stays out: the operator's own accounts may share an address with a client's.
/// </summary>
public interface IClientTenantEmails
{
    /// <summary>
    /// Whether a client tenant other than <paramref name="exceptTenantId"/> already has a user with this e-mail,
    /// compared as Identity normalizes it. Pass null to look in every client tenant.
    /// </summary>
    Task<bool> IsInUseByAnotherClientTenantAsync(string email, string? exceptTenantId, CancellationToken cancellationToken);
}
