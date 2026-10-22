using FSH.Framework.Eventing.Abstractions;
using FSH.Modules.Identity.Contracts.v1.Impersonation;

namespace FSH.Modules.Identity.Contracts.Events;

/// <summary>
/// Integration event raised once when an impersonation grant becomes terminal, by the actor ending it
/// or by an operator revoking it. Expiry raises nothing: no code path observes it.
/// <see cref="IIntegrationEvent.TenantId"/> is the impersonated tenant, the one whose data the grant touched.
/// <c>Motivo</c> is how the grant terminated: <see cref="ImpersonationGrantStatus.Ended"/> or <see cref="ImpersonationGrantStatus.Revoked"/>.
/// </summary>
public sealed record ImpersonationGrantTerminatedIntegrationEvent(
    Guid Id,
    DateTime OccurredOnUtc,
    string? TenantId,
    string CorrelationId,
    string Source,
    Guid GrantId,
    string Jti,
    string ImpersonatedUserId,
    ImpersonationGrantStatus Motivo)
    : IIntegrationEvent;
