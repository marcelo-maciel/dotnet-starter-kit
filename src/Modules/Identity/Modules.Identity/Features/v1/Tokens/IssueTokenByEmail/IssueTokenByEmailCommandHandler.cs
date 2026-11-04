using System.Security.Cryptography;
using System.Text;
using Finbuckle.MultiTenant;
using Finbuckle.MultiTenant.Abstractions;
using FSH.Framework.Core.Context;
using FSH.Framework.Core.Exceptions;
using FSH.Framework.Shared.Multitenancy;
using FSH.Modules.Auditing.Contracts;
using FSH.Modules.Identity.Contracts.v1.Tokens.IssueTokenByEmail;
using FSH.Modules.Identity.Contracts.v1.Tokens.TokenGeneration;
using FSH.Modules.Identity.Domain;
using FSH.Modules.Identity.Services;
using Mediator;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FSH.Modules.Identity.Features.v1.Tokens.IssueTokenByEmail;

/// <summary>
/// Login without tenant (O2). The e-mail names at most one client tenant (O1); the login then runs the kit's
/// <see cref="GenerateTokenCommand"/> inside that tenant's scope, so the PAF pipeline (licence, single session,
/// the refusal of a user with no unidade) and the session bound to the token run exactly as on <c>token/issue</c>.
/// Order of the answers, so nobody without the password learns anything:
/// no tenant, look-alike address, deactivated tenant, root (outside the lookup) and a locked account cost one decoy
/// hash and give the generic 401; a wrong password gives the same 401; only after the password matched may the kit
/// flow reveal an expired tenant, a deactivated account, a missing second factor or the PAF refusals. Every answer
/// past the rate limiter takes at least <see cref="ResponseFloor.Floor"/>.
/// </summary>
public sealed class IssueTokenByEmailCommandHandler(
    IServiceScopeFactory scopeFactory,
    ClientTenantEmailDirectory emails,
    EmailLoginRateLimiter rateLimiter,
    IRequestContext requestContext,
    IPasswordHasher<FshUser> passwordHasher,
    ILogger<IssueTokenByEmailCommandHandler> logger)
    : ICommandHandler<IssueTokenByEmailCommand, IssueTokenByEmailResponse>
{
    public async ValueTask<IssueTokenByEmailResponse> Handle(IssueTokenByEmailCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        // The same trim and NFC the kit's FindByEmail path applies, so both logins read the same address.
        var email = command.Email.Trim().Normalize();
        // Outside the floor: a 429 depends on the counters alone, and holding it would only keep floods connected.
        rateLimiter.ThrowIfLoginExceeded(requestContext.IpAddress, email);

        return await ResponseFloor.AtLeastAsync(() => SignInAsync(email, command, cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>Short hash of the normalized e-mail: the audit records the attempt without the personal data.</summary>
    public static string UnknownAccountSubject(string email)
    {
        ArgumentNullException.ThrowIfNull(email);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(email.ToUpperInvariant()));
        return $"email:{Convert.ToHexString(hash.AsSpan(0, 8))}";
    }

    private async Task<IssueTokenByEmailResponse> SignInAsync(
        string email,
        IssueTokenByEmailCommand command,
        CancellationToken cancellationToken)
    {
        var tenant = await emails.FindSignInTenantAsync(email, cancellationToken).ConfigureAwait(false);
        if (tenant is null)
        {
            LoginGuards.VerifyDecoy(passwordHasher, command.Password);
            await AuditUnknownAccountAsync(email, cancellationToken).ConfigureAwait(false);
            throw new UnauthorizedException();
        }

        var token = await IssueInTenantAsync(tenant, email, command, cancellationToken).ConfigureAwait(false);
        return new IssueTokenByEmailResponse(
            tenant.Id,
            token.AccessToken,
            token.RefreshToken,
            token.RefreshTokenExpiresAt,
            token.AccessTokenExpiresAt);
    }

    // The Finbuckle context is an AsyncLocal: it is set here, in the method that sends the command, and does not
    // leak back into the anonymous request once this returns.
    private async Task<Contracts.DTOs.TokenResponse> IssueInTenantAsync(
        AppTenantInfo tenant,
        string email,
        IssueTokenByEmailCommand command,
        CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var services = scope.ServiceProvider;
        services.GetRequiredService<IMultiTenantContextSetter>().MultiTenantContext =
            new MultiTenantContext<AppTenantInfo>(tenant);

        var userManager = services.GetRequiredService<UserManager<FshUser>>();
        var user = await userManager.FindByEmailAsync(email).ConfigureAwait(false);
        if (user is null)
        {
            // Removed between the lookup and here: still the unknown e-mail's answer and cost.
            LoginGuards.VerifyDecoy(passwordHasher, command.Password);
            throw new UnauthorizedException();
        }

        // M1: locked is answered like a wrong password, without checking it (the decoy keeps the time) and without
        // counting another failure; the kit flow below would say 423 to anyone holding the e-mail.
        if (await userManager.IsLockedOutAsync(user).ConfigureAwait(false))
        {
            LoginGuards.VerifyDecoy(passwordHasher, command.Password);
            await AuditFailedLoginAsync(services, user.Id, "LockedOut", cancellationToken).ConfigureAwait(false);
            throw new UnauthorizedException();
        }

        if (!await userManager.CheckPasswordAsync(user, command.Password).ConfigureAwait(false))
        {
            await LoginGuards.RecordFailedAttemptAsync(userManager, user, logger).ConfigureAwait(false);
            await AuditFailedLoginAsync(services, user.Id, "InvalidCredentials", cancellationToken).ConfigureAwait(false);
            throw new UnauthorizedException();
        }

        return await services.GetRequiredService<IMediator>()
            .Send(new GenerateTokenCommand(email, command.Password, command.TwoFactorCode), cancellationToken)
            .ConfigureAwait(false);
    }

    // Written through the tenant's scope: the row lands in that tenant's audit, where its admin sees it.
    private async Task AuditFailedLoginAsync(IServiceProvider services, string userId, string reason, CancellationToken cancellationToken)
    {
        await services.GetRequiredService<ISecurityAudit>().LoginFailedAsync(
            subjectIdOrName: userId,
            clientId: requestContext.ClientId,
            reason: reason,
            ip: requestContext.IpAddress ?? "unknown",
            ct: cancellationToken).ConfigureAwait(false);
    }

    // Written to root's audit: the attempt names no tenant, and only a hash of the e-mail lands there.
    private async Task AuditUnknownAccountAsync(string email, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var root = await services.GetRequiredService<IMultiTenantStore<AppTenantInfo>>()
            .GetAsync(MultitenancyConstants.Root.Id)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("The root tenant is missing from the tenant store.");
        services.GetRequiredService<IMultiTenantContextSetter>().MultiTenantContext =
            new MultiTenantContext<AppTenantInfo>(root);
        await services.GetRequiredService<ISecurityAudit>().LoginFailedAsync(
            subjectIdOrName: UnknownAccountSubject(email),
            clientId: requestContext.ClientId,
            reason: "InvalidCredentials",
            ip: requestContext.IpAddress ?? "unknown",
            ct: cancellationToken).ConfigureAwait(false);
    }
}
