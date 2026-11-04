using FSH.Framework.Shared.Multitenancy;
using FSH.Modules.Identity.Contracts.Services;
using FSH.Modules.Identity.Data;
using FSH.Modules.Identity.Domain;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.Identity.Services;

/// <summary>The client user that holds an e-mail: its tenant and the address exactly as stored.</summary>
internal sealed record ClientEmailHolder(string TenantId, string Email);

/// <summary>
/// Looks an e-mail up across the client tenants, root excluded (O1). The unique partial index
/// <c>IX_Users_NormalizedEmail_Clientes</c> makes at most one client user hold a normalized e-mail; this is
/// what the validations and the login without tenant read before the index has to refuse anything.
/// </summary>
internal sealed class ClientTenantEmailDirectory(IdentityDbContext db, ILookupNormalizer normalizer) : IClientTenantEmails
{
    /// <summary>The key Finbuckle 10 gives its tenant query filter (PafEmailUnicoEntreClientesTests pins it).</summary>
    public const string TenantFilterKey = "__tenant__";

    public async Task<bool> IsInUseByAnotherClientTenantAsync(string email, string? exceptTenantId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(email);

        var holders = ClientUsers(email);
        if (exceptTenantId is not null)
        {
            holders = holders.Where(u => EF.Property<string>(u, "TenantId") != exceptTenantId);
        }

        return await holders.AnyAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The single client user with this normalized e-mail, or null. More than one only happens before the
    /// index exists; refusing then is the safe side, since the login would otherwise pick a tenant.
    /// </summary>
    public async Task<ClientEmailHolder?> FindHolderAsync(string email, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(email);

        var holders = await ClientUsers(email)
            .Select(u => new { TenantId = EF.Property<string>(u, "TenantId"), u.Email })
            .Take(2)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return holders is [{ Email: { } stored } only]
            ? new ClientEmailHolder(only.TenantId, stored)
            : null;
    }

    // Only the tenant filter goes, by name: the parameterless IgnoreQueryFilters() would also drop any filter added
    // to Users later (a soft delete, say), the trap the architecture notes record.
    private IQueryable<FshUser> ClientUsers(string email)
    {
        var normalized = normalizer.NormalizeEmail(email.Trim());
        return db.Users
            .IgnoreQueryFilters([TenantFilterKey])
            .AsNoTracking()
            .Where(u => u.NormalizedEmail == normalized
                && EF.Property<string>(u, "TenantId") != MultitenancyConstants.Root.Id);
    }
}
