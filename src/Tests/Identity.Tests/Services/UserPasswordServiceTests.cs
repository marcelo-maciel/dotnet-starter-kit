using System.Linq.Expressions;
using Finbuckle.MultiTenant.Abstractions;
using FSH.Framework.Jobs.Services;
using FSH.Framework.Mailing;
using FSH.Framework.Mailing.Services;
using FSH.Framework.Shared.Multitenancy;
using FSH.Modules.Identity.Domain;
using FSH.Modules.Identity.Services;
using Identity.Tests.Support;
using Microsoft.AspNetCore.Identity;
using NSubstitute;

namespace Identity.Tests.Services;

/// <summary>
/// Tests for UserPasswordService.ForgotPasswordAsync — focuses on the reset-link format
/// (regression cover for the double-slash, missing-tenant and unencoded-email defects).
/// </summary>
public sealed class UserPasswordServiceTests
{
    private const string TenantId = "codefi";

    private readonly UserManager<FshUser> _userManager;
    private readonly IJobService _jobService;
    private readonly IMailService _mailService;
    private readonly IMultiTenantContextAccessor<AppTenantInfo> _tenantAccessor;

    public UserPasswordServiceTests()
    {
        _userManager = Substitute.For<UserManager<FshUser>>(
            Substitute.For<IUserStore<FshUser>>(), null, null, null, null, null, null, null, null);
        _jobService = Substitute.For<IJobService>();
        _mailService = Substitute.For<IMailService>();
        _tenantAccessor = Substitute.For<IMultiTenantContextAccessor<AppTenantInfo>>();

        var mtContext = Substitute.For<IMultiTenantContext<AppTenantInfo>>();
        mtContext.TenantInfo.Returns(new AppTenantInfo(TenantId, TenantId, "Codefi"));
        _tenantAccessor.MultiTenantContext.Returns(mtContext);

        // The mail job is enqueued as an expression; compile + invoke it so the captured MailRequest
        // reaches the (mocked) mail service exactly as production would build it.
        _jobService.Enqueue(Arg.Any<Expression<Func<Task>>>())
            .Returns(ci =>
            {
                ci.Arg<Expression<Func<Task>>>().Compile().Invoke();
                return "job-1";
            });
        _mailService.SendAsync(Arg.Any<MailRequest>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
    }

    private UserPasswordService CreateSut() =>
        new(_userManager, null!, _jobService, _mailService, _tenantAccessor, null!, null!, DeploymentCultureOptions.For("en-US"), SharedResourcesLocalizerFactory.Create());

    private MailRequest CaptureSentMail()
    {
        var call = _mailService.ReceivedCalls().Single();
        return (MailRequest)call.GetArguments()[0]!;
    }

    [Fact]
    public async Task ForgotPasswordAsync_Should_BuildResetLink_WithSingleSlash_Tenant_AndEncodedEmail()
    {
        // Arrange — trailing slash on the origin (as Uri.ToString() produces for a host-only URL) and an
        // email with reserved characters ('+', '@') to exercise all three defects at once.
        const string email = "marcelo+reset@codefi.com.br";
        var user = new FshUser { Email = email, UserName = email };
        _userManager.FindByEmailAsync(email).Returns(user);
        _userManager.GeneratePasswordResetTokenAsync(user).Returns("raw-token");

        var sut = CreateSut();

        // Act
        await sut.ForgotPasswordAsync(email, "https://appbase.codefi.com.br/", CancellationToken.None);

        // Assert — the link is asserted on the text/plain alternative, which carries the URL verbatim.
        // The HTML part necessarily escapes '&' as '&amp;' (see the anchor test below).
        var body = CaptureSentMail().TextBody!;
        body.ShouldContain("https://appbase.codefi.com.br/reset-password?");
        body.ShouldNotContain("//reset-password");                       // defect 3: no double slash
        body.ShouldContain($"&tenant={TenantId}");                       // defect 4: tenant present
        // defect 5: reserved chars are encoded — '+' must become %2B (an unencoded '+' would decode to a
        // space). '@' is left as-is, which is valid in a query component per RFC 3986 (QueryHelpers encodes
        // only what is required, matching GetEmailVerificationUriAsync).
        body.ShouldContain("email=marcelo%2Breset");
        body.ShouldNotContain("email=marcelo+reset");                    // raw '+' must not leak
    }

    [Fact]
    public async Task ForgotPasswordAsync_Should_SendTheLinkAsAnAnchor_Not_BareText()
    {
        // Arrange — the body travels as text/html (BodyBuilder.HtmlBody / SendGrid htmlContent), and most
        // clients do not auto-link a bare URL inside an HTML part: it reaches the user as dead text.
        const string email = "operator@codefi.com.br";
        var user = new FshUser { Email = email, UserName = email };
        _userManager.FindByEmailAsync(email).Returns(user);
        _userManager.GeneratePasswordResetTokenAsync(user).Returns("raw-token");

        var sut = CreateSut();

        // Act
        await sut.ForgotPasswordAsync(email, "https://adminbase.codefi.com.br", CancellationToken.None);

        // Assert
        var html = CaptureSentMail().Body!;
        html.ShouldContain("<a href=\"https://adminbase.codefi.com.br/reset-password?");
        // Inside HTML the query separator must be the entity, or the parser eats the following parameter.
        html.ShouldContain("&amp;tenant=");
        html.ShouldNotContain("&tenant=");
    }

    [Fact]
    public async Task ForgotPasswordAsync_Should_SendATextAlternative_AlongsideTheHtml()
    {
        // Arrange — HTML-only mail leaves text-only clients with nothing and scores worse with spam filters.
        const string email = "operator@codefi.com.br";
        var user = new FshUser { Email = email, UserName = email };
        _userManager.FindByEmailAsync(email).Returns(user);
        _userManager.GeneratePasswordResetTokenAsync(user).Returns("raw-token");

        var sut = CreateSut();

        // Act
        await sut.ForgotPasswordAsync(email, "https://adminbase.codefi.com.br", CancellationToken.None);

        // Assert
        var mail = CaptureSentMail();
        mail.TextBody.ShouldNotBeNullOrWhiteSpace();
        mail.TextBody.ShouldContain("https://adminbase.codefi.com.br/reset-password?");
        mail.TextBody.ShouldNotContain("<a ");
        mail.TextBody.ShouldNotContain("&amp;");
    }

    [Fact]
    public async Task ForgotPasswordAsync_Should_NotEnqueueMail_When_UserIsUnknown()
    {
        // Arrange — anti-enumeration: unknown user silently no-ops (no mail), still a 200 upstream.
        _userManager.FindByEmailAsync(Arg.Any<string>()).Returns((FshUser?)null);
        var sut = CreateSut();

        // Act
        await sut.ForgotPasswordAsync("ghost@codefi.com.br", "https://appbase.codefi.com.br/", CancellationToken.None);

        // Assert
        _jobService.DidNotReceive().Enqueue(Arg.Any<Expression<Func<Task>>>());
    }

    // The integration host swaps the exception handler, so the key the 500 body is translated from is pinned here.
    [Theory]
    [InlineData("en-US", "Failed to change password.")]
    [InlineData("pt-BR", "Falha ao alterar a senha.")]
    public async Task ChangePasswordAsync_Should_CarryTheCatalogKey_When_IdentityRefusesTheChange(string culture, string detail)
    {
        // Arrange
        var user = new FshUser { Id = "u1", Email = "ana@codefi.com.br", UserName = "ana" };
        _userManager.FindByIdAsync("u1").Returns(user);
        _userManager.ChangePasswordAsync(user, "errada", "Nova456!")
            .Returns(IdentityResult.Failed(new IdentityError { Code = "PasswordMismatch", Description = "Senha incorreta." }));
        var sut = CreateSut();

        // Act
        var ex = await Should.ThrowAsync<FSH.Framework.Core.Exceptions.CustomException>(
            () => sut.ChangePasswordAsync("errada", "Nova456!", "Nova456!", "u1", CancellationToken.None));

        // Assert
        ex.MessageKey.ShouldBe("Identity.FailedToChangePassword");
        ex.ResourceSource.ShouldBe(typeof(FSH.Modules.Identity.Localization.IdentityResources));
        ex.ErrorMessages.ShouldBe(["Senha incorreta."]);
        using var scope = new UiCultureScope(culture);
        IdentityResourcesLocalizerFactory.Create()[ex.MessageKey!].Value.ShouldBe(detail);
    }
}
