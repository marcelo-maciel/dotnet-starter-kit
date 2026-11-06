using System.Globalization;
using Finbuckle.MultiTenant.Abstractions;
using FSH.Framework.Eventing.Abstractions;
using FSH.Framework.Mailing.Services;
using FSH.Framework.Shared.Multitenancy;
using FSH.Modules.Billing.Contracts.Events;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FSH.Modules.Notifications.IntegrationEventHandlers;

/// <summary>Emails the tenant admin when an invoice is issued. Resolves the admin email from the tenant
/// store (the event only carries the tenant id).</summary>
public sealed class InvoiceIssuedEmailHandler(
    IMultiTenantStore<AppTenantInfo> tenantStore,
    IMailService mailService,
    ILogger<InvoiceIssuedEmailHandler> logger,
    IOptions<RequestLocalizationOptions> localizationOptions)
    : IIntegrationEventHandler<InvoiceIssuedIntegrationEvent>
{
    // The recipient is the tenant's AdminEmail, a string on the tenant record: reading that user's own
    // locale would cross into Identity from a background handler, so the deployment's default culture
    // (pt-BR in production) decides.
    private CultureInfo DeploymentCulture => localizationOptions.Value.DefaultRequestCulture.UICulture;

    public async Task HandleAsync(InvoiceIssuedIntegrationEvent @event, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(@event);
        if (string.IsNullOrWhiteSpace(@event.TenantId))
        {
            return;
        }

        var tenant = await tenantStore.GetAsync(@event.TenantId).ConfigureAwait(false);
        if (tenant is null)
        {
            return;
        }

        var (subject, body, textBody) = BillingEmailBodies.InvoiceIssued(
            @event.InvoiceNumber, @event.Amount, @event.Currency, @event.DueAtUtc, DeploymentCulture);
        await BillingEmailSender.SendAsync(mailService, logger, tenant.AdminEmail, subject, body, textBody, "invoice-issued", ct)
            .ConfigureAwait(false);
    }
}
