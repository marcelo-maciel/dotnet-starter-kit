using System.Globalization;
using System.Linq.Expressions;
using Finbuckle.MultiTenant;
using Finbuckle.MultiTenant.Abstractions;
using FSH.Framework.Eventing.Outbox;
using FSH.Framework.Jobs.Services;
using FSH.Framework.Mailing;
using FSH.Framework.Mailing.Services;
using FSH.Framework.Shared.Multitenancy;
using FSH.Modules.Identity.Contracts.Events;
using FSH.Modules.Identity.Domain;
using FSH.Modules.Identity.Events;
using FSH.Modules.Identity.Localization;
using FSH.Modules.Identity.Services;
using Identity.Tests.Support;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Identity.Tests.Services;

/// <summary>
/// The welcome, confirm-email and reset-password e-mails are written in the recipient's stored locale, or
/// in the deployment's default culture (pt-BR in production) when the user has none or an unsupported one.
/// Never in the ambient culture: the assembly pins it to en-US (TestUiCulture), so every Portuguese
/// expectation below fails if a body is built from CurrentUICulture.
/// </summary>
public sealed class IdentityEmailCultureTests
{
    private const string Email = "marcelo@codefi.com.br";

    private readonly UserManager<FshUser> _userManager = Substitute.For<UserManager<FshUser>>(
        Substitute.For<IUserStore<FshUser>>(), null, null, null, null, null, null, null, null);
    private readonly IJobService _jobService = Substitute.For<IJobService>();
    private readonly IMailService _mailService = Substitute.For<IMailService>();
    private readonly IMultiTenantContextAccessor<AppTenantInfo> _tenant = Substitute.For<IMultiTenantContextAccessor<AppTenantInfo>>();

    public IdentityEmailCultureTests()
    {
        _tenant.MultiTenantContext.Returns(new MultiTenantContext<AppTenantInfo>(new AppTenantInfo("root", "root")));

        // The mail jobs are enqueued as expressions; run them so the built MailRequest reaches the mock.
        _jobService.Enqueue(Arg.Any<Expression<Func<Task>>>()).Returns(ci =>
        {
            ci.Arg<Expression<Func<Task>>>().Compile().Invoke();
            return "job-1";
        });
        _jobService.Enqueue(Arg.Any<string>(), Arg.Any<Expression<Func<Task>>>()).Returns(ci =>
        {
            ci.Arg<Expression<Func<Task>>>().Compile().Invoke();
            return "job-1";
        });
        _mailService.SendAsync(Arg.Any<MailRequest>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
    }

    private MailRequest SentMail() => (MailRequest)_mailService.ReceivedCalls().Single().GetArguments()[0]!;

    // ---- reset password ----

    [Theory]
    [InlineData("pt-BR", "en-US", "Redefinição de senha", "Redefina sua senha pelo link a seguir: ")]
    [InlineData(null, "pt-BR", "Redefinição de senha", "Redefina sua senha pelo link a seguir: ")]
    [InlineData("fr-FR", "pt-BR", "Redefinição de senha", "Redefina sua senha pelo link a seguir: ")]
    [InlineData("en-US", "pt-BR", "Reset Password", "Please reset your password using the following link: ")]
    [InlineData(null, "en-US", "Reset Password", "Please reset your password using the following link: ")]
    public async Task ResetPassword_Should_FollowTheUsersLocale_ElseTheDeploymentDefault(
        string? userLocale, string deploymentDefault, string subject, string textStart)
    {
        // Arrange
        var user = new FshUser { Email = Email, UserName = Email, Locale = userLocale };
        _userManager.FindByEmailAsync(Email).Returns(user);
        _userManager.GeneratePasswordResetTokenAsync(user).Returns("raw-token");
        var sut = new UserPasswordService(_userManager, null!, _jobService, _mailService, _tenant, null!, null!, DeploymentCultureOptions.For(deploymentDefault));

        // Act
        await sut.ForgotPasswordAsync(Email, "https://app.test", CancellationToken.None);

        // Assert
        var mail = SentMail();
        mail.Subject.ShouldBe(subject);
        mail.TextBody!.ShouldStartWith(textStart);
    }

    [Fact]
    public async Task ResetPassword_Should_LeaveNoEnglishInTheHtml_ForAPortugueseUser()
    {
        // Arrange
        var user = new FshUser { Email = Email, UserName = Email, Locale = "pt-BR" };
        _userManager.FindByEmailAsync(Email).Returns(user);
        _userManager.GeneratePasswordResetTokenAsync(user).Returns("raw-token");
        var sut = new UserPasswordService(_userManager, null!, _jobService, _mailService, _tenant, null!, null!, DeploymentCultureOptions.For("en-US"));

        // Act
        await sut.ForgotPasswordAsync(Email, "https://app.test", CancellationToken.None);

        // Assert: heading, intro, button and the fallback sentence above the bare address.
        var html = SentMail().Body!;
        html.ShouldContain(HtmlEmail.Encode("Redefina sua senha"));
        html.ShouldContain(HtmlEmail.Encode("Use o link abaixo para escolher uma nova senha."));
        html.ShouldContain(HtmlEmail.Encode("Redefinir senha") + "</a>");
        html.ShouldContain(HtmlEmail.Encode("Se o botão não funcionar, copie este endereço no navegador:"));
        html.ShouldNotContain("If the button does not work");
        html.ShouldNotContain("Reset", Case.Sensitive);
    }

    // ---- confirm e-mail ----

    [Theory]
    [InlineData(null, "pt-BR", "Confirme seu e-mail", "Confirme seu e-mail pelo link a seguir: ")]
    [InlineData("pt-BR", "en-US", "Confirme seu e-mail", "Confirme seu e-mail pelo link a seguir: ")]
    [InlineData("en-US", "pt-BR", "Confirm Your Email Address", "Please confirm your email address using the following link: ")]
    public async Task ConfirmEmail_Should_FollowTheUsersLocale_ElseTheDeploymentDefault(
        string? userLocale, string deploymentDefault, string subject, string textStart)
    {
        // Arrange
        var user = new FshUser { Id = "u1", Email = Email, UserName = "marcelo", FirstName = "Marcelo", EmailConfirmed = false, Locale = userLocale };
        _userManager.Users.Returns(new[] { user }.AsAsyncQueryable());
        _userManager.GenerateEmailConfirmationTokenAsync(user).Returns("raw-token");
        var sut = new UserRegistrationService(_userManager, db: null!, _jobService, _mailService, _tenant, Substitute.For<IOutboxStore>(), DeploymentCultureOptions.For(deploymentDefault));

        // Act
        await sut.ResendConfirmationEmailAsync("u1", "https://app.test", CancellationToken.None);

        // Assert
        var mail = SentMail();
        mail.Subject.ShouldBe(subject);
        mail.TextBody!.ShouldStartWith(textStart);
    }

    [Fact]
    public async Task ConfirmEmail_Should_LeaveNoEnglishInTheHtml_ForAPortugueseUser()
    {
        // Arrange
        var user = new FshUser { Id = "u1", Email = Email, UserName = "marcelo", FirstName = "<b>Marcelo</b>", EmailConfirmed = false, Locale = null };
        _userManager.Users.Returns(new[] { user }.AsAsyncQueryable());
        _userManager.GenerateEmailConfirmationTokenAsync(user).Returns("raw-token");
        var sut = new UserRegistrationService(_userManager, db: null!, _jobService, _mailService, _tenant, Substitute.For<IOutboxStore>(), DeploymentCultureOptions.For("pt-BR"));

        // Act
        await sut.ResendConfirmationEmailAsync("u1", "https://app.test", CancellationToken.None);

        // Assert
        var html = SentMail().Body!;
        html.ShouldContain(HtmlEmail.Encode("Olá, <b>Marcelo</b>,"));
        html.ShouldNotContain("<b>Marcelo</b>");
        html.ShouldContain(HtmlEmail.Encode("Agradecemos seu cadastro! Confirme seu e-mail clicando no botão abaixo:"));
        html.ShouldContain(HtmlEmail.Encode("Confirmar e-mail"));
        html.ShouldContain(HtmlEmail.Encode("Esta é uma mensagem automática. Não responda a este e-mail."));
        foreach (var english in new[] { "Confirm Your", "Hi ", "Thank you", "If the button", "If you didn", "automated message" })
        {
            html.ShouldNotContain(english, Case.Sensitive);
        }
    }

    // ---- welcome ----

    [Theory]
    [InlineData(null, "pt-BR", "Bem-vindo(a)!", "Olá, Marcelo! Agradecemos seu cadastro.")]
    [InlineData("pt-BR", "en-US", "Bem-vindo(a)!", "Olá, Marcelo! Agradecemos seu cadastro.")]
    [InlineData("en-US", "pt-BR", "Welcome!", "Hi Marcelo, thanks for registering.")]
    public async Task Welcome_Should_FollowTheEventsLocale_ElseTheDeploymentDefault(
        string? locale, string deploymentDefault, string subject, string text)
    {
        // Arrange
        var sut = new UserRegisteredEmailHandler(_mailService, NullLogger<UserRegisteredEmailHandler>.Instance, DeploymentCultureOptions.For(deploymentDefault));
        var @event = new UserRegisteredIntegrationEvent(
            Id: Guid.NewGuid(),
            OccurredOnUtc: DateTime.UtcNow,
            TenantId: "root",
            CorrelationId: Guid.NewGuid().ToString(),
            Source: "test",
            UserId: Guid.NewGuid().ToString(),
            Email: Email,
            FirstName: "Marcelo",
            LastName: "Maciel",
            Locale: locale);

        // Act
        await sut.HandleAsync(@event, CancellationToken.None);

        // Assert
        var mail = SentMail();
        mail.Subject.ShouldBe(subject);
        mail.TextBody.ShouldBe(text);
    }

    [Fact]
    public void RecordRegistered_Should_CarryTheUsersLocale_IntoTheEvent()
    {
        // The welcome e-mail can only follow the locale if the domain event hands it over.
        var user = new FshUser { Id = "u1", Email = Email, Locale = "pt-BR" };

        user.RecordRegistered("root");

        user.DomainEvents.OfType<FSH.Modules.Identity.Domain.Events.UserRegisteredEvent>().Single().Locale.ShouldBe("pt-BR");
    }

    [Fact]
    public void CultureFor_Should_ReturnTheDeploymentDefault_ForAnUnsupportedOrMiscasedTag()
    {
        var ptBr = CultureInfo.GetCultureInfo("pt-BR");

        IdentityEmails.CultureFor("en-US", ptBr).Name.ShouldBe("en-US");
        IdentityEmails.CultureFor("pt", ptBr).ShouldBe(ptBr);
        IdentityEmails.CultureFor("EN-us", ptBr).ShouldBe(ptBr);
        IdentityEmails.CultureFor(string.Empty, ptBr).ShouldBe(ptBr);
    }
}
