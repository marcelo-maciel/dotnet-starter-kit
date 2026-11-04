using Finbuckle.MultiTenant.Abstractions;
using FSH.Framework.Shared.Multitenancy;
using FSH.Modules.Identity.Contracts.Services;
using FSH.Modules.Identity.Domain;
using Microsoft.AspNetCore.Identity;

namespace FSH.Modules.Identity.Services;

/// <summary>
/// Refuses, on every create and update of a client user, an e-mail another client tenant already uses (O1).
/// Identity runs it from UserManager, so the kit's registration, the PAF cadastro, the tenant admin seeded at
/// provisioning and any later e-mail change all pass here; the unique index is the backstop for a race.
/// </summary>
internal sealed class ClientTenantEmailValidator(
    IClientTenantEmails emails,
    IMultiTenantContextAccessor<AppTenantInfo> tenantAccessor) : IUserValidator<FshUser>
{
    public const string ErrorCode = "EmailInUseByAnotherClientTenant";

    // Never names the tenant: the person creating the user must not learn which company has the address.
    public const string Mensagem = "Este e-mail já está em uso.";

    public async Task<IdentityResult> ValidateAsync(UserManager<FshUser> manager, FshUser user)
    {
        ArgumentNullException.ThrowIfNull(user);

        var tenantId = tenantAccessor.MultiTenantContext?.TenantInfo?.Id;
        if (string.IsNullOrWhiteSpace(user.Email)
            || string.IsNullOrWhiteSpace(tenantId)
            || string.Equals(tenantId, MultitenancyConstants.Root.Id, StringComparison.Ordinal))
        {
            return IdentityResult.Success;
        }

        // IUserValidator takes no token; the query is one indexed lookup under the connection's command timeout.
        var inUse = await emails
            .IsInUseByAnotherClientTenantAsync(user.Email, tenantId, CancellationToken.None)
            .ConfigureAwait(false);
        return inUse
            ? IdentityResult.Failed(new IdentityError { Code = ErrorCode, Description = Mensagem })
            : IdentityResult.Success;
    }
}
