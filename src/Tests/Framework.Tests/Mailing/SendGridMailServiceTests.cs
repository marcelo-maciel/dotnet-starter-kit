using System.Collections.ObjectModel;
using System.Net;
using FSH.Framework.Mailing;
using FSH.Framework.Mailing.Services;
using Microsoft.Extensions.Options;
using NSubstitute;
using SendGrid;
using SendGrid.Helpers.Mail;

namespace Framework.Tests.Mailing;

// REL-01 repro: SendGridMailService discards the Response from SendEmailAsync and the client is
// built with HttpErrorAsException=false, so a non-2xx SendGrid reply is swallowed and the caller
// believes the mail was delivered. This test pins the DESIRED behaviour (a non-success status must
// surface) and is expected to FAIL against the current code — that failure IS the confirmation.
public sealed class SendGridMailServiceTests
{
    private static SendGridMailService BuildService(ISendGridClient client)
    {
        var options = Options.Create(new MailOptions
        {
            UseSendGrid = true,
            From = "noreply@x.com",
            SendGrid = new SendGridOptions { ApiKey = "sg-key", From = "noreply@x.com" },
        });
        return new SendGridMailService(options, client);
    }

    private static MailRequest ValidRequest() =>
        new(to: ["dest@x.com"], subject: "hi", body: "body");

    [Fact]
    public async Task SendAsync_When_SendGridReturnsNonSuccess_Should_SurfaceFailure()
    {
        // Arrange
        var client = Substitute.For<ISendGridClient>();
        client.SendEmailAsync(Arg.Any<SendGridMessage>(), Arg.Any<CancellationToken>())
            .Returns(new Response(HttpStatusCode.Unauthorized, null, null));
        var service = BuildService(client);

        // Act
        var send = async () => await service.SendAsync(ValidRequest(), CancellationToken.None);

        // Assert — a 401 from SendGrid must NOT be reported as a successful send.
        await send.ShouldThrowAsync<Exception>();
    }

    [Fact]
    public async Task SendAsync_When_SendGridReturnsAccepted_Should_Complete()
    {
        // Arrange
        var client = Substitute.For<ISendGridClient>();
        client.SendEmailAsync(Arg.Any<SendGridMessage>(), Arg.Any<CancellationToken>())
            .Returns(new Response(HttpStatusCode.Accepted, null, null));
        var service = BuildService(client);

        // Act
        var send = async () => await service.SendAsync(ValidRequest(), CancellationToken.None);

        // Assert
        await send.ShouldNotThrowAsync();
    }
}
