using System.Globalization;
using FSH.Framework.Eventing.Abstractions;
using FSH.Framework.Mailing.Services;
using FSH.Modules.Multitenancy.Contracts.Events;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FSH.Modules.Notifications.IntegrationEventHandlers;

/// <summary>Emails the tenant admin that their subscription expired and access is suspended.</summary>
public sealed class TenantExpiredEmailHandler(
    IMailService mailService,
    ILogger<TenantExpiredEmailHandler> logger,
    IOptions<RequestLocalizationOptions> localizationOptions)
    : IIntegrationEventHandler<TenantExpiredIntegrationEvent>
{
    // The recipient is the tenant's AdminEmail, a string on the tenant record: reading that user's own
    // locale would cross into Identity from a background handler, so the deployment's default culture
    // (pt-BR in production) decides.
    private CultureInfo DeploymentCulture => localizationOptions.Value.DefaultRequestCulture.UICulture;

    public async Task HandleAsync(TenantExpiredIntegrationEvent @event, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(@event);
        var (subject, body, textBody) = BillingEmailBodies.Expired(@event.TenantName, @event.PlanKey, @event.ValidUpto, DeploymentCulture);
        await BillingEmailSender.SendAsync(mailService, logger, @event.AdminEmail, subject, body, textBody, "expired", ct)
            .ConfigureAwait(false);
    }
}
