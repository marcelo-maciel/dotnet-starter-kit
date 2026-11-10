using FSH.Framework.Eventing.Abstractions;
using FSH.Modules.Identity.Contracts.Events;
using FSH.Modules.Identity.Domain.Events;
using Mediator;
using Microsoft.Extensions.Logging;

namespace FSH.Modules.Identity.Events;

/// <summary>
/// Handles the SessionRevokedEvent domain event by publishing an integration event
/// so other modules can react to the revoked session.
/// </summary>
public sealed class SessionRevokedHandler(
    IOutboxWriter outbox,
    ILogger<SessionRevokedHandler> logger)
    : INotificationHandler<SessionRevokedEvent>
{
    public async ValueTask Handle(SessionRevokedEvent notification, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(notification);

        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation(
                "Session {SessionId} revoked for user {UserId} by {RevokedBy}: {Reason}",
                notification.SessionId,
                notification.UserId,
                notification.RevokedBy,
                notification.Reason);
        }

        // UserSession.Revoke raises the domain event only on the first revocation, so one revoked
        // session yields one row; the event id is the outbox key.
        var integrationEvent = new SessionRevokedIntegrationEvent(
            Id: notification.EventId,
            OccurredOnUtc: notification.OccurredOnUtc.UtcDateTime,
            TenantId: notification.TenantId,
            CorrelationId: notification.CorrelationId ?? notification.EventId.ToString(),
            Source: nameof(SessionRevokedHandler),
            SessionId: notification.SessionId,
            UserId: notification.UserId,
            Reason: notification.Reason);

        await outbox.AddAsync(integrationEvent, cancellationToken).ConfigureAwait(false);
    }
}
