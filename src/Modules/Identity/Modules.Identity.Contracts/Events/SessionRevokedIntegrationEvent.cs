using FSH.Framework.Eventing.Abstractions;

namespace FSH.Modules.Identity.Contracts.Events;

/// <summary>
/// Integration event raised once per revoked user session, by any revocation path (self-service,
/// admin, revoke-all), so other modules can drop what they hold for that session.
/// Delivery is not guaranteed: the domain event is published after the revocation is saved and a
/// handler failure is only logged, so consumers must not rely on it as the only revocation check.
/// </summary>
public sealed record SessionRevokedIntegrationEvent(
    Guid Id,
    DateTime OccurredOnUtc,
    string? TenantId,
    string CorrelationId,
    string Source,
    Guid SessionId,
    string UserId,
    string? Reason)
    : IIntegrationEvent;
