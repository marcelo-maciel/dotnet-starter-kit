namespace FSH.Framework.Eventing.Abstractions;

/// <summary>
/// The publish side of the transactional outbox — all a module ever needs.
///
/// Deliberately separate from the full store: draining is expressed in terms of the persisted
/// message entity and belongs to the eventing runtime, while publishing is just "record this
/// event". Keeping the writer here means a module depends on the eventing abstractions only,
/// exactly as it did when it published straight to <see cref="IEventBus"/>.
///
/// Prefer this over <see cref="IEventBus"/> in module code: the row commits with the caller's
/// transaction and survives a crash, where a direct publish does neither.
/// </summary>
public interface IOutboxWriter
{
    /// <summary>Records an integration event for durable, at-least-once delivery.</summary>
    Task AddAsync(IIntegrationEvent @event, CancellationToken ct = default);
}
