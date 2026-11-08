using Finbuckle.MultiTenant.Abstractions;
using FSH.Framework.Core.Common;
using FSH.Framework.Core.Exceptions;
using FSH.Framework.Jobs.Services;
using FSH.Framework.Mailing;
using FSH.Framework.Mailing.Services;
using FSH.Framework.Shared.Constants;
using FSH.Framework.Shared.Multitenancy;
using FSH.Modules.Identity.Contracts.Services;
using FSH.Modules.Identity.Data;
using FSH.Modules.Identity.Domain;
using FSH.Modules.Identity.Localization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Net;
using System.Security.Claims;
using System.Text;

namespace FSH.Modules.Identity.Services;

internal sealed class UserRegistrationService(
    UserManager<FshUser> userManager,
    IdentityDbContext db,
    IJobService jobService,
    IMailService mailService,
    IMultiTenantContextAccessor<AppTenantInfo> multiTenantContextAccessor,
    IOptions<RequestLocalizationOptions> localizationOptions,
    IStringLocalizer<IdentityResources> localizer) : IUserRegistrationService
{
    // Every new account starts in the deployment's language (pt-BR in production), so the e-mails it gets
    // and the panel it opens agree before the user ever picks one.
    private CultureInfo DeploymentCulture => localizationOptions.Value.DefaultRequestCulture.UICulture;

    public async Task<string> GetOrCreateFromPrincipalAsync(ClaimsPrincipal principal, CancellationToken cancellationToken = default)
    {
        EnsureValidTenant();
        ArgumentNullException.ThrowIfNull(principal);

        var email = ExtractEmailFromPrincipal(principal);

        var existingUser = await userManager.FindByEmailAsync(email);
        if (existingUser is not null)
        {
            return existingUser.Id;
        }

        var user = await CreateUserFromPrincipalAsync(principal, email);
        await AssignDefaultRoleAndGroupsAsync(user, "ExternalAuth", cancellationToken);
        await PublishUserRegisteredAsync(user, cancellationToken);

        return user.Id;
    }

    public async Task<string> RegisterAsync(
        string firstName,
        string lastName,
        string email,
        string userName,
        string password,
        string confirmPassword,
        string phoneNumber,
        string origin,
        CancellationToken cancellationToken)
    {
        ValidatePasswordMatch(password, confirmPassword);

        var user = await CreateUserWithPasswordAsync(firstName, lastName, email, userName, password, phoneNumber);
        await AssignDefaultRoleAndGroupsAsync(user, "System", cancellationToken);
        await SendConfirmationEmailAsync(user, origin, cancellationToken);
        await PublishUserRegisteredAsync(user, cancellationToken);

        return user.Id;
    }

    public async Task<string> ConfirmEmailAsync(string userId, string code, string tenant, CancellationToken cancellationToken)
    {
        EnsureValidTenant();

        var user = await userManager.Users
            .Where(u => u.Id == userId && !u.EmailConfirmed)
            .FirstOrDefaultAsync(cancellationToken);

        _ = user ?? throw new CustomException("An error occurred while confirming E-Mail.")
            .Localized<IdentityResources>("Identity.EmailConfirmationError");

        code = Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(code));
        var result = await userManager.ConfirmEmailAsync(user, code);

        return result.Succeeded
            // The success text is what the confirm-email page shows the user, so it localizes like the
            // failure below; the old developer hint about /api/tokens is gone from the user's screen.
            ? localizer["Identity.EmailConfirmed", user.Email!].Value
            : throw new CustomException(string.Format(CultureInfo.InvariantCulture, "An error occurred while confirming {0}", user.Email))
                .Localized<IdentityResources>("Identity.EmailConfirmationFailedFor", user.Email!);
    }

    public async Task AdminConfirmEmailAsync(string userId, CancellationToken cancellationToken = default)
    {
        EnsureValidTenant();

        var user = await userManager.Users
            .Where(u => u.Id == userId)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new NotFoundException($"User {userId} was not found.")
                .Localized<IdentityResources>("Identity.UserNotFoundById", userId);

        // Idempotent: a second confirm is a no-op rather than an error.
        if (user.EmailConfirmed)
        {
            return;
        }

        user.EmailConfirmed = true;
        var result = await userManager.UpdateAsync(user);
        if (!result.Succeeded)
        {
            var errors = string.Join("; ", result.Errors.Select(e => e.Description));
            throw new CustomException(string.Format(
                CultureInfo.InvariantCulture,
                "An error occurred while confirming the email for {0}: {1}",
                user.Email,
                errors))
                .Localized<IdentityResources>("Identity.EmailConfirmationFailedWithErrors", user.Email!, errors);
        }
    }

    public async Task ResendConfirmationEmailAsync(string userId, string origin, CancellationToken cancellationToken = default)
    {
        EnsureValidTenant();

        var user = await userManager.Users
            .Where(u => u.Id == userId)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new NotFoundException($"User {userId} was not found.")
                .Localized<IdentityResources>("Identity.UserNotFoundById", userId);

        if (user.EmailConfirmed)
        {
            throw new CustomException(string.Format(
                CultureInfo.InvariantCulture,
                "The email for {0} is already confirmed.",
                user.Email))
                .Localized<IdentityResources>("Identity.EmailAlreadyConfirmed", user.Email!);
        }

        await SendConfirmationEmailAsync(user, origin, cancellationToken);
    }

    public async Task<string> ConfirmPhoneNumberAsync(string userId, string code, CancellationToken cancellationToken = default)
    {
        EnsureValidTenant();

        var user = await userManager.Users
            .Where(u => u.Id == userId && !u.PhoneNumberConfirmed)
            .FirstOrDefaultAsync(cancellationToken);

        _ = user ?? throw new CustomException("An error occurred while confirming phone number.")
            .Localized<IdentityResources>("Identity.PhoneConfirmationError");

        code = Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(code));
        var result = await userManager.ChangePhoneNumberAsync(user, user.PhoneNumber!, code);

        return result.Succeeded
            ? string.Format(CultureInfo.InvariantCulture, "Phone number {0} confirmed successfully.", user.PhoneNumber)
            : throw new CustomException(string.Format(CultureInfo.InvariantCulture, "An error occurred while confirming phone number {0}", user.PhoneNumber))
                .Localized<IdentityResources>("Identity.PhoneConfirmationFailedFor", user.PhoneNumber!);
    }

    private void EnsureValidTenant()
    {
        if (string.IsNullOrWhiteSpace(multiTenantContextAccessor?.MultiTenantContext?.TenantInfo?.Id))
        {
            throw new UnauthorizedException("invalid tenant").Localized("Error.InvalidTenant");
        }
    }

    private static string ExtractEmailFromPrincipal(ClaimsPrincipal principal)
    {
        return principal.FindFirstValue(ClaimTypes.Email)
            ?? principal.FindFirstValue("email")
            ?? throw new CustomException("Email claim is required for external authentication.")
                .Localized<IdentityResources>("Identity.EmailClaimRequired");
    }

    private async Task<FshUser> CreateUserFromPrincipalAsync(ClaimsPrincipal principal, string email)
    {
        var (firstName, lastName, userName) = ExtractUserInfoFromPrincipal(principal, email);

        userName = await EnsureUniqueUserNameAsync(userName);

        var user = new FshUser
        {
            Email = email,
            UserName = userName,
            FirstName = firstName,
            LastName = lastName,
            EmailConfirmed = true,
            PhoneNumberConfirmed = false,
            IsActive = true,
            Locale = DeploymentCulture.Name,
        };

        var result = await userManager.CreateAsync(user);
        if (!result.Succeeded)
        {
            var errors = result.Errors.Select(e => e.Description).ToList();
            throw new CustomException(
                "Failed to create user from external principal.",
                errors,
                HttpStatusCode.BadRequest)
                .Localized<IdentityResources>("Identity.FailedToCreateUserFromPrincipal");
        }

        return user;
    }

    private static (string firstName, string lastName, string userName) ExtractUserInfoFromPrincipal(
        ClaimsPrincipal principal, string email)
    {
        var firstName = principal.FindFirstValue(ClaimTypes.GivenName)
            ?? principal.FindFirstValue("given_name")
            ?? string.Empty;

        var lastName = principal.FindFirstValue(ClaimTypes.Surname)
            ?? principal.FindFirstValue("family_name")
            ?? string.Empty;

        var userName = principal.FindFirstValue(ClaimTypes.Name)
            ?? principal.FindFirstValue("preferred_username")
            ?? email.Split('@')[0];

        return (firstName, lastName, userName);
    }

    private async Task<string> EnsureUniqueUserNameAsync(string userName)
    {
        if (await userManager.FindByNameAsync(userName) is not null)
        {
            return $"{userName}_{Guid.NewGuid():N}"[..20];
        }
        return userName;
    }

    private static void ValidatePasswordMatch(string password, string confirmPassword)
    {
        if (password != confirmPassword)
        {
            throw new CustomException(
                "Passwords do not match.",
                errors: null,
                HttpStatusCode.BadRequest)
                .Localized<IdentityResources>("Identity.PasswordsDoNotMatch");
        }
    }

    private async Task<FshUser> CreateUserWithPasswordAsync(
        string firstName,
        string lastName,
        string email,
        string userName,
        string password,
        string phoneNumber)
    {
        var user = new FshUser
        {
            Email = email,
            FirstName = firstName,
            LastName = lastName,
            UserName = userName,
            PhoneNumber = phoneNumber,
            IsActive = true,
            EmailConfirmed = false,
            PhoneNumberConfirmed = false,
            Locale = DeploymentCulture.Name,
        };

        var result = await userManager.CreateAsync(user, password);
        if (!result.Succeeded)
        {
            // Identity create failures (duplicate email/username, password policy, …) are
            // client-input errors, not server faults — surface them as 400 with the specific
            // reasons so the caller sees *why* registration failed, not a bare 500.
            var errors = result.Errors.Select(error => error.Description).ToList();
            throw new CustomException(
                "Unable to register the user.",
                errors,
                HttpStatusCode.BadRequest)
                .Localized<IdentityResources>("Identity.UnableToRegisterUser");
        }

        return user;
    }

    private async Task AssignDefaultRoleAndGroupsAsync(
        FshUser user,
        string source,
        CancellationToken cancellationToken = default)
    {
        await userManager.AddToRoleAsync(user, RoleConstants.Basic);

        var defaultGroups = await db.Groups
            .AsNoTracking()
            .Where(g => g.IsDefault && !g.IsDeleted)
            .ToListAsync(cancellationToken);

        foreach (var group in defaultGroups)
        {
            db.UserGroups.Add(UserGroup.Create(user.Id, group.Id, source));
        }

        if (defaultGroups.Count > 0)
        {
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    private async Task SendConfirmationEmailAsync(FshUser user, string origin, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(user.Email))
        {
            return;
        }

        string emailVerificationUri = await GetEmailVerificationUriAsync(user, origin);
        var culture = IdentityEmails.CultureFor(user.Locale, DeploymentCulture);
        var (subject, html, text) = IdentityEmails.ConfirmEmail(user.FirstName ?? user.UserName, emailVerificationUri, culture);

        var mailRequest = new MailRequest(
            new Collection<string> { user.Email },
            subject,
            html,
            textBody: text);

        jobService.Enqueue("email", () => mailService.SendAsync(mailRequest, cancellationToken));
    }

    // The save dispatches UserRegisteredEvent, and UserRegisteredHandler turns it into the one
    // UserRegisteredIntegrationEvent. Writing a second one here gave it a different id, so the inbox
    // could not deduplicate it and every consumer (the welcome e-mail) ran twice.
    private async Task PublishUserRegisteredAsync(
        FshUser user,
        CancellationToken cancellationToken = default)
    {
        var tenantId = multiTenantContextAccessor.MultiTenantContext.TenantInfo?.Id;
        user.RecordRegistered(tenantId);

        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task<string> GetEmailVerificationUriAsync(FshUser user, string origin)
    {
        EnsureValidTenant();

        string code = await userManager.GenerateEmailConfirmationTokenAsync(user);
        code = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(code));

        // Point at the SPA confirm-email page on the front-end the user registered from, which in turn
        // calls the API. The origin argument is the already-resolved front-end origin.
        const string route = "confirm-email";
        var endpointUri = new Uri(string.Concat($"{origin.TrimEnd('/')}/", route));

        string verificationUri = QueryHelpers.AddQueryString(endpointUri.ToString(), QueryStringKeys.UserId, user.Id);
        verificationUri = QueryHelpers.AddQueryString(verificationUri, QueryStringKeys.Code, code);
        verificationUri = QueryHelpers.AddQueryString(
            verificationUri,
            MultitenancyConstants.Identifier,
            multiTenantContextAccessor?.MultiTenantContext?.TenantInfo?.Id!);

        return verificationUri;
    }
}