using FSH.Framework.Eventing.Abstractions;

namespace FSH.Framework.Eventing.Outbox;

/// <summary>
/// Abstraction for persisting and reading outbox messages. Modules only ever need the publish
/// side (<see cref="IOutboxWriter"/>); the rest of this surface belongs to the dispatcher.
/// </summary>
public interface IOutboxStore : IOutboxWriter
{
    /// <summary>
    /// Atomically leases up to <paramref name="batchSize"/> pending messages to this dispatcher
    /// and returns them. Rows already leased by another instance are skipped rather than waited
    /// on, so instances never block each other and never both publish the same message.
    /// </summary>
    /// <param name="batchSize">Maximum number of messages to claim.</param>
    /// <param name="claimedBy">Diagnostic identifier of the claiming dispatcher instance.</param>
    /// <param name="lease">How long the claim holds before another instance may re-claim.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<OutboxMessage>> ClaimBatchAsync(
        int batchSize,
        string claimedBy,
        TimeSpan lease,
        CancellationToken ct = default);

    Task MarkAsProcessedAsync(OutboxMessage message, CancellationToken ct = default);

    Task MarkAsFailedAsync(OutboxMessage message, string error, bool isDead, CancellationToken ct = default);

    /// <summary>Lists dead-lettered (exhausted) messages so an operator can inspect them.</summary>
    Task<IReadOnlyList<OutboxMessage>> GetDeadLetteredAsync(int max, CancellationToken ct = default);

    /// <summary>
    /// Resets dead-lettered messages for another dispatch attempt (clears the dead flag, retry
    /// count, last error and backoff). Pass specific ids, or <c>null</c> to redrive them all.
    /// Returns the number of messages redriven.
    /// </summary>
    Task<int> RedriveDeadLettersAsync(IReadOnlyCollection<Guid>? ids, CancellationToken ct = default);
}