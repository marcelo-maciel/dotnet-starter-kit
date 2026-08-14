namespace FSH.Framework.Eventing.Abstractions;

/// <summary>
/// Enumerates the distinct databases holding outbox rows.
///
/// The framework default returns a single target (the configured connection). The multitenancy
/// module replaces it with one that also returns each distinct active per-tenant connection
/// string — without it, a tenant with a dedicated database writes outbox rows the dispatcher
/// never looks at.
/// </summary>
public interface IEventingDrainTargetProvider
{
    Task<IReadOnlyList<EventingDrainTarget>> GetTargetsAsync(CancellationToken ct = default);
}
