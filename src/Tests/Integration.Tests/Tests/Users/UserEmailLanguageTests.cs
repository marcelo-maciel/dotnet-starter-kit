using Finbuckle.MultiTenant;
using Finbuckle.MultiTenant.Abstractions;
using FSH.Framework.Eventing.Persistence;
using FSH.Framework.Mailing;
using FSH.Framework.Mailing.Services;
using FSH.Framework.Shared.Multitenancy;
using FSH.Modules.Identity.Contracts.Events;
using FSH.Modules.Identity.Domain;
using Hangfire;
using Integration.Tests.Infrastructure;
using Integration.Tests.Tests.Sessions;
using Microsoft.AspNetCore.Identity;

namespace Integration.Tests.Tests.Users;

/// <summary>
/// Every account stores a language from the moment it is created (the deployment's default culture, en-US
/// in this host, which leaves LocalizationOptions:DefaultCulture unset), and the account e-mails follow it
/// through the real pipeline: the endpoint, the Hangfire mail job and the outbox. The pt-BR deployment
/// default itself is covered by the unit tests, since this host's default is read at startup.
/// </summary>
[Collection(FshCollectionDefinition.Name)]
public sealed class UserEmailLanguageTests
{
    private readonly FshWebApplicationFactory _factory;
    private readonly AuthHelper _auth;

    public UserEmailLanguageTests(FshWebApplicationFactory factory)
    {
        _factory = factory;
        _auth = new AuthHelper(factory);
    }

    private NoOpMailService Mail => (NoOpMailService)_factory.Services.GetRequiredService<IMailService>();

    [Fact]
    public async Task Register_Should_StoreTheDeploymentDefaultLocale_AndCarryItIntoTheOutbox()
    {
        // Arrange
        using var admin = await _auth.CreateRootAdminClientAsync();

        // Act
        var user = await IdentityUserSeeder.CreateLoginableUserAsync(_factory, admin, "locale-new");

        // Assert
        (await LocaleOfAsync(user.Email)).ShouldBe("en-US");
        // Registration here still writes two UserRegisteredIntegrationEvent rows (the domain event's and
        // PublishUserRegisteredAsync's own), so every row has to carry the locale.
        var payloads = await RegisteredEventPayloadsAsync(user.UserId);
        payloads.ShouldNotBeEmpty();
        payloads.ShouldAllBe(p => p.Contains("\"en-US\""));
    }

    [Fact]
    public async Task Register_Should_SendTheConfirmationAndTheWelcome_InTheNewUsersLanguage()
    {
        // Arrange
        using var admin = await _auth.CreateRootAdminClientAsync();
        Mail.Clear();

        // Act
        var user = await IdentityUserSeeder.CreateLoginableUserAsync(_factory, admin, "locale-mails");
        await OutboxDrain.DrainAsync(_factory.Services);

        // Assert
        (await WaitForMailAsync(user.Email, "Confirm Your Email Address")).TextBody!
            .ShouldStartWith("Please confirm your email address using the following link: ");
        (await WaitForMailAsync(user.Email, "Welcome!")).TextBody.ShouldBe("Hi Seeded, thanks for registering.");
    }

    [Theory]
    [InlineData("pt-BR", "Redefinição de senha", "Redefina sua senha pelo link a seguir: ")]
    [InlineData("en-US", "Reset Password", "Please reset your password using the following link: ")]
    public async Task ForgotPassword_Should_MailInTheUsersLanguage_NotTheCallers(string locale, string subject, string textStart)
    {
        // Arrange: the anonymous caller asks in the other language; the account holder's choice wins.
        using var admin = await _auth.CreateRootAdminClientAsync();
        var user = await IdentityUserSeeder.CreateLoginableUserAsync(_factory, admin, "locale-reset");
        await SetLocaleAsync(user.Email, locale);
        Mail.Clear();

        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("tenant", TestConstants.RootTenantId);
        client.DefaultRequestHeaders.Add("Accept-Language", locale == "pt-BR" ? "en-US" : "pt-BR");

        // Act
        var response = await client.PostAsJsonAsync($"{TestConstants.IdentityBasePath}/forgot-password", new { email = user.Email });

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await WaitForMailAsync(user.Email, subject)).TextBody!.ShouldStartWith(textStart);
        MailsTo(user.Email).Where(m => m.Subject is "Reset Password" or "Redefinição de senha").ShouldAllBe(m => m.Subject == subject);
    }

    private async Task<MailRequest> WaitForMailAsync(string to, string subject)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            var mail = MailsTo(to).FirstOrDefault(m => m.Subject == subject);
            if (mail is not null)
            {
                return mail;
            }

            await Task.Delay(100);
        }

        var seen = string.Join(" | ", MailsTo(to).Select(m => m.Subject));
        throw new ShouldAssertException($"No mail \"{subject}\" to {to} within 30 s; subjects built for it: [{seen}]");
    }

    /// <summary>
    /// Every mail built for the address: the ones the outbox handlers sent straight to the capturing mail
    /// service (the welcome), plus the MailRequest arguments of the Hangfire jobs the API enqueued (reset and
    /// confirmation). The job arguments are read from the storage, not from the capture, because in this host
    /// those jobs reach Succeeded without ever showing up in NoOpMailService.Sent (observed 22/10/2026: no
    /// existing test asserted a Hangfire-delivered mail). What is asserted is exactly what the API handed to
    /// the job.
    /// </summary>
    private List<MailRequest> MailsTo(string to)
    {
        var monitor = JobStorage.Current.GetMonitoringApi();
        var jobs = monitor.SucceededJobs(0, 1000).Select(j => j.Value.Job)
            .Concat(monitor.ProcessingJobs(0, 1000).Select(j => j.Value.Job))
            .Concat(monitor.FailedJobs(0, 1000).Select(j => j.Value.Job))
            .Concat(monitor.Queues().SelectMany(q => monitor.EnqueuedJobs(q.Name, 0, 1000)).Select(j => j.Value.Job));
        return Mail.Sent
            .Concat(jobs.Where(j => j is not null).SelectMany(j => j.Args.OfType<MailRequest>()))
            .Where(m => m.To.Contains(to))
            .ToList();
    }

    // The tenant context is an AsyncLocal: set inside a helper it would not reach the caller, so each
    // reader below enters the root tenant itself, right before its query.

    private async Task<string?> LocaleOfAsync(string email)
    {
        using var scope = _factory.Services.CreateScope();
        var tenant = await scope.ServiceProvider.GetRequiredService<IMultiTenantStore<AppTenantInfo>>().GetAsync(TestConstants.RootTenantId);
        scope.ServiceProvider.GetRequiredService<IMultiTenantContextSetter>().MultiTenantContext = new MultiTenantContext<AppTenantInfo>(tenant);
        var user = await scope.ServiceProvider.GetRequiredService<UserManager<FshUser>>().FindByEmailAsync(email);
        user.ShouldNotBeNull();
        return user.Locale;
    }

    private async Task SetLocaleAsync(string email, string locale)
    {
        using var scope = _factory.Services.CreateScope();
        var tenant = await scope.ServiceProvider.GetRequiredService<IMultiTenantStore<AppTenantInfo>>().GetAsync(TestConstants.RootTenantId);
        scope.ServiceProvider.GetRequiredService<IMultiTenantContextSetter>().MultiTenantContext = new MultiTenantContext<AppTenantInfo>(tenant);
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<FshUser>>();
        var user = await userManager.FindByEmailAsync(email);
        user.ShouldNotBeNull();
        user.Locale = locale;
        (await userManager.UpdateAsync(user)).Succeeded.ShouldBeTrue();
    }

    private async Task<List<string>> RegisteredEventPayloadsAsync(string userId)
    {
        using var scope = _factory.Services.CreateScope();
        var tenant = await scope.ServiceProvider.GetRequiredService<IMultiTenantStore<AppTenantInfo>>().GetAsync(TestConstants.RootTenantId);
        scope.ServiceProvider.GetRequiredService<IMultiTenantContextSetter>().MultiTenantContext = new MultiTenantContext<AppTenantInfo>(tenant);
        var context = scope.ServiceProvider.GetRequiredService<EventingDbContext>();
        return await context.OutboxMessages
            .Where(m => m.Type.Contains(nameof(UserRegisteredIntegrationEvent)) && m.Payload.Contains(userId))
            .Select(m => m.Payload)
            .ToListAsync();
    }
}
