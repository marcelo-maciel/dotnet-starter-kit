using FSH.Framework.Eventing.Abstractions;

namespace FSH.Modules.Identity.Contracts.Events;

/// <summary>
/// Integration event raised when a new user is registered. <c>Locale</c> is the user's stored UI
/// language tag, so the welcome e-mail is written in it; it is null on events written before the field
/// existed, and consumers then fall back to the deployment's default culture.
/// </summary>
public sealed record UserRegisteredIntegrationEvent(
    Guid Id,
    DateTime OccurredOnUtc,
    string? TenantId,
    string CorrelationId,
    string Source,
    string UserId,
    string Email,
    string FirstName,
    string LastName,
    string? Locale)
    : IIntegrationEvent;