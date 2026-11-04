using Finbuckle.MultiTenant;
using Finbuckle.MultiTenant.Abstractions;
using FSH.Framework.Core.Context;
using FSH.Framework.Shared.Multitenancy;
using FSH.Framework.Web.Frontend;
using FSH.Modules.Identity.Contracts.Services;
using FSH.Modules.Identity.Contracts.v1.Users.ForgotPasswordByEmail;
using FSH.Modules.Identity.Localization;
using FSH.Modules.Identity.Services;
using Mediator;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;

namespace FSH.Modules.Identity.Features.v1.Users.ForgotPasswordByEmail;

/// <summary>
/// Forgot password without tenant (O3): finds the client tenant of the e-mail as the login does and runs the kit's
/// own forgot-password inside it, so the link carries that tenant and the reset page keeps reading it. The answer
/// is the same whether or not the e-mail exists, whether or not the e-mail's hourly allowance is spent, and it takes
/// at least <see cref="ResponseFloor.Floor"/>, so the known address's extra work (token, enqueued e-mail) does not
/// show in the time.
/// </summary>
public sealed class ForgotPasswordByEmailCommandHandler(
    IServiceScopeFactory scopeFactory,
    ClientTenantEmailDirectory emails,
    EmailLoginRateLimiter rateLimiter,
    IRequestContext requestContext,
    IFrontendOriginResolver originResolver,
    IStringLocalizer<IdentityResources> localizer)
    : ICommandHandler<ForgotPasswordByEmailCommand, string>
{
    public async ValueTask<string> Handle(ForgotPasswordByEmailCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var email = command.Email.Trim().Normalize();
        // The IP window may answer 429: it says nothing about the address.
        rateLimiter.ThrowIfForgotIpExceeded(requestContext.IpAddress);

        // Self-service flow: the reset link must land on the SPA the user is currently using.
        var origin = originResolver.ResolveForCurrentRequest();
        await ResponseFloor.AtLeastAsync(() => SendIfKnownAsync(email, origin, cancellationToken)).ConfigureAwait(false);
        return localizer["Identity.PasswordResetEmailSent"];
    }

    private async Task<bool> SendIfKnownAsync(string email, string origin, CancellationToken cancellationToken)
    {
        // Over the e-mail's allowance nothing is sent, and the caller gets the same answer: a flood at one mailbox
        // stops without telling whether the mailbox has an account.
        if (!rateLimiter.TryAcquireForgotEmail(email))
        {
            return false;
        }

        var tenant = await emails.FindSignInTenantAsync(email, cancellationToken).ConfigureAwait(false);
        if (tenant is null)
        {
            return false;
        }

        await SendInTenantAsync(tenant, email, origin, cancellationToken).ConfigureAwait(false);
        return true;
    }

    // The Finbuckle context is an AsyncLocal: set in the method that uses it.
    private async Task SendInTenantAsync(AppTenantInfo tenant, string email, string origin, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var services = scope.ServiceProvider;
        services.GetRequiredService<IMultiTenantContextSetter>().MultiTenantContext =
            new MultiTenantContext<AppTenantInfo>(tenant);
        await services.GetRequiredService<IUserPasswordService>()
            .ForgotPasswordAsync(email, origin, cancellationToken)
            .ConfigureAwait(false);
    }
}
