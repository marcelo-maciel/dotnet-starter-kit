using FSH.Framework.Eventing.Abstractions;

namespace FSH.Framework.Eventing;

/// <summary>
/// Default provider for single-database deployments: one pass over the configured connection.
/// The multitenancy module replaces this when per-tenant databases are in play.
/// </summary>
public sealed class SingleDatabaseDrainTargetProvider : IEventingDrainTargetProvider
{
    private static readonly IReadOnlyList<EventingDrainTarget> DefaultOnly =
        [new EventingDrainTarget(null, null)];

    public Task<IReadOnlyList<EventingDrainTarget>> GetTargetsAsync(CancellationToken ct = default)
        => Task.FromResult(DefaultOnly);
}
