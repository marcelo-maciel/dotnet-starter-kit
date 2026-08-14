namespace FSH.Framework.Eventing.Abstractions;

/// <summary>
/// Installs the ambient tenant context — including the dedicated connection string — for the
/// duration of one drain pass, so an <c>EventingDbContext</c> resolved inside that scope targets
/// the right database.
///
/// Distinct from <see cref="IEventTenantScope"/>, which sets tenant identity only and is
/// deliberately connection-string-agnostic: that one runs when dispatching an event to handlers,
/// this one runs when choosing which database to read the outbox from.
/// </summary>
public interface IEventingDrainScope
{
    IDisposable Begin(EventingDrainTarget target);
}
