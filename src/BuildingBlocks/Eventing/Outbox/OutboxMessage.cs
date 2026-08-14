using FSH.Framework.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FSH.Framework.Eventing.Outbox;

/// <summary>
/// Outbox message entity used to persist integration events alongside domain changes.
///
/// Implements <see cref="IGlobalEntity"/> to opt out of automatic tenant
/// filtering: outbox processors run in background scopes without a tenant
/// context and must scan rows across tenants. Per-row tenant association
/// is kept in the explicit nullable <see cref="TenantId"/> column.
/// </summary>
public class OutboxMessage : IGlobalEntity
{
    public Guid Id { get; set; }

    public DateTime CreatedOnUtc { get; set; }

    public string Type { get; set; } = default!;

    public string Payload { get; set; } = default!;

    public string? TenantId { get; set; }

    public string? CorrelationId { get; set; }

    public DateTime? ProcessedOnUtc { get; set; }

    public int RetryCount { get; set; }

    public string? LastError { get; set; }

    public bool IsDead { get; set; }

    /// <summary>
    /// Earliest UTC time the message is eligible for another dispatch attempt. Set to a
    /// backed-off future time after a failure so retries space out instead of re-firing every
    /// dispatch cycle; <c>null</c> means immediately eligible (fresh message or after a redrive).
    /// </summary>
    public DateTime? NextRetryAt { get; set; }

    /// <summary>
    /// Lease expiry. A dispatcher claims a row by stamping this into the future; another instance
    /// may re-claim only once it has passed, so a crashed dispatcher's rows are recovered rather
    /// than stranded. <c>null</c> means unclaimed.
    /// </summary>
    public DateTime? ClaimedUntilUtc { get; set; }

    /// <summary>Identifier of the dispatcher instance holding the lease. Diagnostic only.</summary>
    public string? ClaimedBy { get; set; }
}

public sealed class OutboxMessageConfiguration : IEntityTypeConfiguration<OutboxMessage>
{
    private readonly string _schema;

    public OutboxMessageConfiguration(string schema)
    {
        _schema = schema;
    }

    public void Configure(EntityTypeBuilder<OutboxMessage> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("OutboxMessages", _schema);

        builder.HasKey(o => o.Id);

        builder.Property(o => o.Type)
            .HasMaxLength(512)
            .IsRequired();

        builder.Property(o => o.Payload)
            .IsRequired();

        builder.Property(o => o.TenantId)
            .HasMaxLength(64);

        builder.Property(o => o.CorrelationId)
            .HasMaxLength(128);

        builder.Property(o => o.CreatedOnUtc)
            .IsRequired();

        builder.Property(o => o.ClaimedBy)
            .HasMaxLength(128);

        // Covers the claim scan: eligible rows are filtered on IsDead/ProcessedOnUtc/ClaimedUntilUtc
        // and ordered by CreatedOnUtc. Without it the claim degrades to a sequential scan under a
        // row lock, which is exactly where contention hurts most.
        builder.HasIndex(o => new { o.IsDead, o.ProcessedOnUtc, o.ClaimedUntilUtc, o.CreatedOnUtc })
            .HasDatabaseName("IX_OutboxMessages_Pending");
    }
}