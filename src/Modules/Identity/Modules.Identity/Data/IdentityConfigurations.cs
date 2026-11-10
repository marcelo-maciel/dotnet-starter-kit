using Finbuckle.MultiTenant.EntityFrameworkCore.Extensions;
using FSH.Framework.Shared.Multitenancy;
using FSH.Modules.Identity.Domain;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FSH.Modules.Identity.Data;

public class ApplicationUserConfig : IEntityTypeConfiguration<FshUser>
{
    public const string ClientEmailIndexName = "IX_Users_NormalizedEmail_Clientes";

    public void Configure(EntityTypeBuilder<FshUser> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder
            .ToTable("Users", IdentityModuleConstants.SchemaName)
            .IsMultiTenant();

        // O1: a client signs in by e-mail alone, so the e-mail is one person across every client tenant. Named,
        // so it sits beside Identity's own EmailIndex instead of replacing it; root stays out of it, because the
        // operator's accounts may share an address with a client's. No IsActive filter: a deactivated user
        // keeps the address.
        builder
            .HasIndex(u => u.NormalizedEmail, ClientEmailIndexName)
                .IsUnique()
                .HasFilter($"\"TenantId\" <> '{MultitenancyConstants.Root.Id}'");

        builder
            .Property(u => u.ObjectId)
                .HasMaxLength(256);

        // A BCP-47 tag is short and bounded; 10 covers language-script-region (zh-Hant-TW). Writes are
        // additionally constrained to SupportedCultures.Tags by UpdateUserCommandValidator, so this is
        // the storage-level backstop, not the validation.
        builder
            .Property(u => u.Locale)
                .HasMaxLength(10);
    }
}

public class ApplicationRoleConfig : IEntityTypeConfiguration<FshRole>
{
    public void Configure(EntityTypeBuilder<FshRole> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder
            .ToTable("Roles", IdentityModuleConstants.SchemaName)
            .IsMultiTenant()
                .AdjustUniqueIndexes();
    }
}

public class ApplicationRoleClaimConfig : IEntityTypeConfiguration<FshRoleClaim>
{
    public void Configure(EntityTypeBuilder<FshRoleClaim> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder
            .ToTable("RoleClaims", IdentityModuleConstants.SchemaName)
            .IsMultiTenant();

        // One row per grant. ClaimValue is nullable and PostgreSQL treats NULLs as distinct here,
        // which is fine: a null-valued claim grants nothing and the permission sync removes it.
        builder
            .HasIndex(rc => new { rc.RoleId, rc.ClaimType, rc.ClaimValue })
            .IsUnique()
            .HasDatabaseName(RoleClaimUniqueness.IndexName);
    }
}

public class IdentityUserRoleConfig : IEntityTypeConfiguration<IdentityUserRole<string>>
{
    public void Configure(EntityTypeBuilder<IdentityUserRole<string>> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder
            .ToTable("UserRoles", IdentityModuleConstants.SchemaName)
            .IsMultiTenant();
    }
}

public class IdentityUserClaimConfig : IEntityTypeConfiguration<IdentityUserClaim<string>>
{
    public void Configure(EntityTypeBuilder<IdentityUserClaim<string>> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder
            .ToTable("UserClaims", IdentityModuleConstants.SchemaName)
            .IsMultiTenant();
    }
}

public class IdentityUserLoginConfig : IEntityTypeConfiguration<IdentityUserLogin<string>>
{
    public void Configure(EntityTypeBuilder<IdentityUserLogin<string>> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder
            .ToTable("UserLogins", IdentityModuleConstants.SchemaName)
            .IsMultiTenant();
    }
}

public class IdentityUserTokenConfig : IEntityTypeConfiguration<IdentityUserToken<string>>
{
    public void Configure(EntityTypeBuilder<IdentityUserToken<string>> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder
            .ToTable("UserTokens", IdentityModuleConstants.SchemaName)
            .IsMultiTenant();
    }
}