using System.Globalization;
using FSH.Framework.Mailing;
using FSH.Framework.Mailing.Services;
using FSH.Modules.Multitenancy.Contracts.Events;
using FSH.Modules.Notifications.IntegrationEventHandlers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Notifications.Tests.IntegrationEventHandlers;

/// <summary>
/// A billing e-mail goes to the tenant's AdminEmail from the outbox or the expiry scan, with no request
/// culture: the deployment's default culture (LocalizationOptions:DefaultCulture, pt-BR in production)
/// decides its language, never the thread's ambient culture.
/// </summary>
public sealed class BillingEmailCultureTests
{
    private readonly IMailService _mail = Substitute.For<IMailService>();

    private static IOptions<RequestLocalizationOptions> DeploymentDefault(string tag) =>
        Options.Create(new RequestLocalizationOptions
        {
            DefaultRequestCulture = new RequestCulture(CultureInfo.InvariantCulture, CultureInfo.GetCultureInfo(tag)),
        });

    private static TenantExpiredIntegrationEvent Expired() => new(
        Id: Guid.NewGuid(),
        OccurredOnUtc: DateTime.UtcNow,
        TenantId: "acme",
        CorrelationId: Guid.NewGuid().ToString(),
        Source: "test",
        TenantName: "Acme",
        AdminEmail: "admin@acme.test",
        PlanKey: "basic",
        ValidUpto: new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc),
        GraceEndsUtc: new DateTime(2026, 5, 8, 0, 0, 0, DateTimeKind.Utc));

    private MailRequest Sent() => (MailRequest)_mail.ReceivedCalls().Single().GetArguments()[0]!;

    [Theory]
    [InlineData("pt-BR", "Sua assinatura expirou")]
    [InlineData("en-US", "Your subscription has expired")]
    public async Task Handler_Should_WriteInTheDeploymentDefaultCulture_WhateverTheAmbientCulture(string deploymentDefault, string subject)
    {
        // Arrange: the ambient culture is the opposite language, so a body built from CurrentUICulture fails.
        var sut = new TenantExpiredEmailHandler(_mail, NullLogger<TenantExpiredEmailHandler>.Instance, DeploymentDefault(deploymentDefault));
        var previous = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(deploymentDefault == "pt-BR" ? "en-US" : "pt-BR");
        try
        {
            // Act
            await sut.HandleAsync(Expired(), CancellationToken.None);
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
        }

        // Assert
        var mail = Sent();
        mail.Subject.ShouldBe(subject);
        mail.TextBody!.ShouldStartWith(subject);
    }
}
