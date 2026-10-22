using System.Reflection;
using FSH.Framework.Jobs.Services;
using FSH.Framework.Mailing.Services;
using FSH.Framework.Web.Modules;
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

namespace PAF.Latencia.Tests;

/// <summary>
/// One API instance on a real Kestrel socket (as <c>PafKestrelFactory</c> of O16) wired to a shared PostgreSQL and a
/// shared Valkey, so the SignalR backplane is on: every group send crosses Valkey, as in production (seção 8). Two of
/// them over the same database and Valkey are the two-instance topology.
/// </summary>
internal sealed class HostLatencia : WebApplicationFactory<Program>
{
    private const string VariavelRedis = "CachingOptions__Redis";
    private static readonly TimeSpan PrazoHttp = TimeSpan.FromSeconds(30);

    private readonly string _postgres;
    private readonly string _valkey;

    public HostLatencia(string postgres, string valkey)
    {
        _postgres = postgres;
        _valkey = valkey;
        UseKestrel(0);
    }

    public Uri Endereco { get; private set; } = default!;

    /// <summary>Starts the host; called once, and never concurrently with another host's start.</summary>
    public void Iniciar()
    {
        // The kit reads CachingOptions:Redis while registering services (AddHeroCaching, AddHeroRealtime), before the
        // configuration overlay below applies; an environment variable is already there (same as PafHostValkey).
        Environment.SetEnvironmentVariable(VariavelRedis, _valkey);
        try
        {
            StartServer();
        }
        finally
        {
            Environment.SetEnvironmentVariable(VariavelRedis, null);
        }

        Services.GetServices<StackExchange.Redis.IConnectionMultiplexer>().ShouldNotBeEmpty("o host precisa subir ligado ao Valkey");
        var enderecos = Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()
            ?? throw new InvalidOperationException("Kestrel não expôs IServerAddressesFeature.");
        Endereco = new Uri(enderecos.Addresses.Single());
    }

    public HttpClient Cliente() => new() { BaseAddress = Endereco, Timeout = PrazoHttp };

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DatabaseOptions:Provider"] = "POSTGRESQL",
            ["DatabaseOptions:ConnectionString"] = _postgres,
            ["DatabaseOptions:MigrationsAssembly"] = "FSH.Starter.Migrations.PostgreSQL",
            ["CachingOptions:Redis"] = _valkey,
            ["JwtOptions:Issuer"] = TestConstants.JwtIssuer,
            ["JwtOptions:Audience"] = TestConstants.JwtAudience,
            ["JwtOptions:SigningKey"] = TestConstants.JwtSigningKey,
            // Longer than the whole run, so no station or person renews its token in the middle of a measurement.
            ["JwtOptions:AccessTokenMinutes"] = "60",
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
            ["HangfireOptions:Password"] = "paf-latencia-test-hangfire-pwd",
            ["HangfireOptions:Route"] = "/jobs",
            ["RateLimitingOptions:Enabled"] = "false",
            ["PasswordPolicy:EnforcePasswordExpiry"] = "false",
            ["Seed:DemoPassword"] = "Password123!",
            ["Seed:DefaultAdminPassword"] = TestConstants.DefaultPassword,
            ["SecurityHeadersOptions:Enabled"] = "false",
        }));

        builder.ConfigureServices(services =>
        {
            // Same removals as the other harnesses. The outbox dispatcher stays off: RNF18 is measured on the direct
            // send after commit (seção 8), the outbox is only its delivery guarantee and wakes every 1 s at best.
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
            services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, o => o.RequireHttpsMetadata = false);
            services.RemoveAll<IMailService>();
            services.AddSingleton<IMailService, NoOpMailService>();
            foreach (var tratador in services.Where(d => d.ServiceType == typeof(Microsoft.AspNetCore.Diagnostics.IExceptionHandler)).ToList())
            {
                services.Remove(tratador);
            }

            services.AddExceptionHandler<DetailedTestExceptionHandler>();
        });
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        builder.UseServiceProviderFactory(new DefaultServiceProviderFactory(new ServiceProviderOptions
        {
            ValidateOnBuild = false,
            ValidateScopes = false,
        }));

        // ModuleLoader keeps the modules in static state: the second instance would register no module service
        // without the reset (same as PafHostValkey). Hosts are started one after the other, never concurrently.
        // Fails loudly if the kit renames the fields: a silent no-op would leave instance B without the PAF.
        var tipo = typeof(ModuleLoader);
        var modulos = tipo.GetField("_modules", BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null) as System.Collections.IList
            ?? throw new InvalidOperationException("ModuleLoader._modules não encontrado.");
        modulos.Clear();
        (tipo.GetField("_modulesLoaded", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("ModuleLoader._modulesLoaded não encontrado.")).SetValue(null, false);
        return base.CreateHost(builder);
    }
}
