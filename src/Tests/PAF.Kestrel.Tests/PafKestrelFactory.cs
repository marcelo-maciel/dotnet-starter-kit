using Finbuckle.MultiTenant;
using Finbuckle.MultiTenant.Abstractions;
using FSH.Framework.Jobs.Services;
using FSH.Framework.Mailing.Services;
using FSH.Framework.Persistence;
using FSH.Framework.Shared.Multitenancy;
using FSH.Modules.Multitenancy.Data;
using Hangfire;
using Hangfire.InMemory;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Testcontainers.PostgreSql;

namespace PAF.Kestrel.Tests;

/// <summary>
/// The API host on a real Kestrel socket (.NET 10 <c>UseKestrel</c>), so the PafHub runs over a real WebSocket
/// upgrade: TestServer closes a WebSocket before the SignalR handshake, which is why Integration.Tests stays on
/// long polling. Same wiring as Integration.Tests' factory for what these tests touch (PostgreSQL container, JWT,
/// in-memory Hangfire for tenant provisioning, no outbox dispatcher), with the access token lasting one minute.
/// One host per process, like the kit's harnesses.
/// </summary>
public sealed class PafKestrelFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    /// <summary>Access token lifetime: the station credential and the people's tokens expire after this.</summary>
    public static readonly TimeSpan ValidadeToken = TimeSpan.FromMinutes(1);

    private static readonly TimeSpan PrazoHttp = TimeSpan.FromSeconds(30);

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine")
        .WithDatabase("fsh_paf_kestrel_tests")
        .WithUsername("postgres")
        .WithPassword("integration_test_pwd")
        .WithAutoRemove(true)
        .WithCleanUp(true)
        .Build();

    public PafKestrelFactory()
    {
        // Port 0: Kestrel picks a free port; read back from IServerAddressesFeature after the start.
        UseKestrel(0);
    }

    /// <summary>Base address of the Kestrel listener (http://127.0.0.1:port).</summary>
    public Uri Endereco { get; private set; } = default!;

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        StartServer();
        var enderecos = Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()
            ?? throw new InvalidOperationException("Kestrel não expôs IServerAddressesFeature.");
        Endereco = new Uri(enderecos.Addresses.Single());
        await ProvisionarTenantRaizAsync();
    }

    public new async Task DisposeAsync()
    {
        await base.DisposeAsync();
        await _postgres.DisposeAsync();
    }

    /// <summary>Client on the real socket, with an explicit deadline.</summary>
    public HttpClient Cliente() => new() { BaseAddress = Endereco, Timeout = PrazoHttp };

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DatabaseOptions:Provider"] = "POSTGRESQL",
                ["DatabaseOptions:ConnectionString"] = _postgres.GetConnectionString(),
                ["DatabaseOptions:MigrationsAssembly"] = "FSH.Starter.Migrations.PostgreSQL",
                ["CachingOptions:Redis"] = "",
                ["JwtOptions:Issuer"] = TestConstants.JwtIssuer,
                ["JwtOptions:Audience"] = TestConstants.JwtAudience,
                ["JwtOptions:SigningKey"] = TestConstants.JwtSigningKey,
                ["JwtOptions:AccessTokenMinutes"] = ValidadeToken.TotalMinutes.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["JwtOptions:RefreshTokenDays"] = "7",
                ["OriginOptions:OriginUrl"] = "http://localhost",
                ["CorsOptions:AllowedOrigins:0"] = "http://localhost",
                ["FrontendOptions:AllowedOrigins:0"] = "http://localhost",
                ["FrontendOptions:DefaultOrigin"] = "http://localhost",
                ["OpenTelemetryOptions:Enabled"] = "false",
                ["EventingOptions:UseHostedServiceDispatcher"] = "false",
                ["Serilog:MinimumLevel:Default"] = "Warning",
                ["Serilog:MinimumLevel:Override:Microsoft.EntityFrameworkCore"] = "Fatal",
                ["Serilog:MinimumLevel:Override:Npgsql"] = "Fatal",
                ["Serilog:WriteTo:0:Name"] = "Console",
                ["Serilog:WriteTo:0:Args:restrictedToMinimumLevel"] = "Warning",
                ["Serilog:WriteTo:1:Name"] = "",
                ["MailOptions:UseSendGrid"] = "false",
                ["HangfireOptions:Username"] = "admin",
                ["HangfireOptions:Password"] = "paf-kestrel-test-hangfire-pwd",
                ["HangfireOptions:Route"] = "/jobs",
                ["RateLimitingOptions:Enabled"] = "false",
                ["PasswordPolicy:EnforcePasswordExpiry"] = "false",
                ["Seed:DemoPassword"] = "Password123!",
                ["Seed:DefaultAdminPassword"] = TestConstants.DefaultPassword,
                ["SecurityHeadersOptions:Enabled"] = "false",
            });
        });

        builder.ConfigureServices(services =>
        {
            // Same removals as Integration.Tests: these need infra the test does not run or race the migrations.
            // Without the outbox dispatcher the kit's session-revoked event never reaches the PAF, so a revoked
            // session is only caught by PafHubFilter on the next call, which is the path under test.
            var remover = services
                .Where(d => d.ServiceType == typeof(IHostedService) &&
                    (d.ImplementationType?.Name == "RolePermissionSyncHostedService" ||
                     d.ImplementationType?.FullName?.Contains("Hangfire", StringComparison.Ordinal) == true ||
                     d.ImplementationType?.Name == "HangfireStaleLockCleanupService" ||
                     d.ImplementationType?.Name == "OutboxDispatcherHostedService"))
                .ToList();
            foreach (var servico in remover)
            {
                services.Remove(servico);
            }

            services.AddHangfire(config => config.UseInMemoryStorage());
            services.AddHangfireServer(options =>
            {
                options.SchedulePollingInterval = TimeSpan.FromSeconds(1);
                options.Queues = ["default", "email"];
                options.WorkerCount = 2;
            });
            services.TryAddTransient<IJobService, HangfireService>();

            services.PostConfigure<JwtBearerOptions>(
                JwtBearerDefaults.AuthenticationScheme,
                options => options.RequireHttpsMetadata = false);

            services.RemoveAll<IMailService>();
            services.AddSingleton<IMailService, NoOpMailService>();

            foreach (var handler in services.Where(d => d.ServiceType == typeof(Microsoft.AspNetCore.Diagnostics.IExceptionHandler)).ToList())
            {
                services.Remove(handler);
            }

            services.AddExceptionHandler<DetailedTestExceptionHandler>();
        });
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        // Same as the kit's harnesses: ValidateOnBuild off for the .NET 10 minimal API dual-host model.
        builder.UseServiceProviderFactory(new DefaultServiceProviderFactory(new ServiceProviderOptions
        {
            ValidateOnBuild = false,
            ValidateScopes = false,
        }));
        return base.CreateHost(builder);
    }

    private async Task ProvisionarTenantRaizAsync()
    {
        using var scope = Services.CreateScope();
        var tenants = scope.ServiceProvider.GetRequiredService<TenantDbContext>();
        await tenants.Database.MigrateAsync();

        var raiz = await tenants.TenantInfo.FindAsync(MultitenancyConstants.Root.Id);
        if (raiz is null)
        {
            raiz = new AppTenantInfo(
                MultitenancyConstants.Root.Id,
                MultitenancyConstants.Root.Name,
                string.Empty,
                MultitenancyConstants.Root.EmailAddress,
                issuer: MultitenancyConstants.Root.Issuer);
            raiz.SetValidity(DateTime.UtcNow.AddYears(1));
            await tenants.TenantInfo.AddAsync(raiz);
            await tenants.SaveChangesAsync();
        }

        scope.ServiceProvider.GetRequiredService<IMultiTenantContextSetter>().MultiTenantContext =
            new MultiTenantContext<AppTenantInfo>(raiz);
        using var prazo = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        foreach (var init in scope.ServiceProvider.GetServices<IDbInitializer>())
        {
            await init.MigrateAsync(prazo.Token);
        }

        foreach (var init in scope.ServiceProvider.GetServices<IDbInitializer>())
        {
            await init.SeedAsync(prazo.Token);
        }

        await scope.ServiceProvider.GetRequiredService<FSH.Modules.Identity.Authorization.RolePermissionSyncer>().SyncAsync(prazo.Token);
    }
}

[CollectionDefinition(Nome)]
public sealed class PafKestrelColecao : ICollectionFixture<PafKestrelFactory>
{
    public const string Nome = "PafKestrel";
}
