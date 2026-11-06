using System.Globalization;
using FSH.Framework.Eventing.Abstractions;
using FSH.Framework.Mailing.Services;
using FSH.Modules.Multitenancy.Contracts.Events;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FSH.Modules.Notifications.IntegrationEventHandlers;

/// <summary>Emails the tenant admin that their subscription is nearing expiry.</summary>
public sealed class TenantNearingExpiryEmailHandler(
    IMailService mailService,
    ILogger<TenantNearingExpiryEmailHandler> logger,
    IOptions<RequestLocalizationOptions> localizationOptions)
    : IIntegrationEventHandler<TenantNearingExpiryIntegrationEvent>
{
    // The recipient is the tenant's AdminEmail, a string on the tenant record: reading that user's own
    // locale would cross into Identity from a background handler, so the deployment's default culture
    // (pt-BR in production) decides.
    private CultureInfo DeploymentCulture => localizationOptions.Value.DefaultRequestCulture.UICulture;

    public async Task HandleAsync(TenantNearingExpiryIntegrationEvent @event, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(@event);
        var (subject, body, textBody) = BillingEmailBodies.NearingExpiry(
            @event.TenantName, @event.PlanKey, @event.ValidUpto, @event.DaysRemaining, DeploymentCulture);
        await BillingEmailSender.SendAsync(mailService, logger, @event.AdminEmail, subject, body, textBody, "nearing-expiry", ct)
            .ConfigureAwait(false);
    }
}
