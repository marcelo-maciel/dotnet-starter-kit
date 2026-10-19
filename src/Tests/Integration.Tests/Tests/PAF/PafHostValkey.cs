using System.Reflection;
using Finbuckle.MultiTenant;
using Finbuckle.MultiTenant.Abstractions;
using FSH.Framework.Jobs.Services;
using FSH.Framework.Mailing.Services;
using FSH.Framework.Persistence;
using FSH.Framework.Shared.Multitenancy;
using FSH.Framework.Web.Modules;
using FSH.Modules.Multitenancy.Data;
using FSH.Modules.PAF.Sessao;
using Hangfire;
using Hangfire.InMemory;
using Integration.Tests.Infrastructure;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Testcontainers.PostgreSql;
using Testcontainers.Redis;

namespace Integration.Tests.Tests.PAF;

/// <summary>
/// Host da API ligado a um Valkey real e pausável, com PostgreSQL próprio e também pausável (S14, seção 9). O host
/// compartilhado do harness sobe sem Valkey (<see cref="FshWebApplicationFactory"/>, arquivo do kit e selado), então
/// o modo degradado com a conexão viva e a volta do Valkey só são exercitados aqui. Mesma imagem do
/// <c>ValkeyPaf</c> de S09 (PAF.Tests), que este projeto não referencia.
/// </summary>
public sealed class PafHostValkey : WebApplicationFactory<Program>, IAsyncLifetime
{
    // The ceilings the environments are expected to set (CONTEXT.md, S14): a paused Valkey costs at most this
    // per call on the paths that do not bound the call themselves (kit idempotency filter, HybridCache L2).
    public const string PrazosValkey = "connectTimeout=2000,syncTimeout=500,asyncTimeout=500,abortConnect=false";

    private const string VariavelRedis = "CachingOptions__Redis";

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine")
        .WithDatabase("fsh_paf_valkey")
        .WithUsername("postgres")
        .WithPassword("integration_test_pwd")
        .WithAutoRemove(true)
        .WithCleanUp(true)
        .Build();

    private readonly RedisContainer _valkey = new RedisBuilder("valkey/valkey:9.1.0-alpine").Build();

    public async Task InitializeAsync()
    {
        await Task.WhenAll(_postgres.StartAsync(), _valkey.StartAsync());

        // The kit reads CachingOptions:Redis while the services are registered (AddHeroCaching, AddHeroRealtime),
        // before the factory's configuration overlay applies; an environment variable is already there. The
        // collection runs alone (DisableParallelization), so no other host is built while it is set.
        Environment.SetEnvironmentVariable(VariavelRedis, $"{_valkey.GetConnectionString()},{PrazosValkey}");
        try
        {
            _ = Server;
        }
        finally
        {
            Environment.SetEnvironmentVariable(VariavelRedis, null);
        }

        Services.GetServices<StackExchange.Redis.IConnectionMultiplexer>().ShouldNotBeEmpty("o host precisa subir ligado ao Valkey");
        await ProvisionarRaizAsync();

        // The hosted rebuild ran before the migrations above and waits for its next tick; a deployed host
        // starts after the migrator, so rebuild now and start every test from a healthy host.
        (await Services.GetRequiredService<ReconstrucaoLicencas>().ReconstruirAsync(CancellationToken.None)).ShouldBeTrue();
    }

    public new async Task DisposeAsync()
    {
        await base.DisposeAsync();
        await _postgres.DisposeAsync();
        await _valkey.DisposeAsync();
    }

    public Task PausarValkeyAsync() => _valkey.PauseAsync();

    public Task RetomarValkeyAsync() => _valkey.UnpauseAsync();

    public Task PausarPostgresAsync() => _postgres.PauseAsync();

    public Task RetomarPostgresAsync() => _postgres.UnpauseAsync();

    protected override void ConfigureClient(HttpClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        client.DefaultRequestHeaders.Add("Origin", "http://localhost");
        base.ConfigureClient(client);
    }

    // The subset of FshWebApplicationFactory this host needs (no S3: no PAF path here touches storage).
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DatabaseOptions:Provider"] = "POSTGRESQL",
            // Explicit deadlines (RNF05): with the server paused, a statement fails instead of hanging the test.
            ["DatabaseOptions:ConnectionString"] = $"{_postgres.GetConnectionString()};Timeout=5;Command Timeout=10",
            ["DatabaseOptions:MigrationsAssembly"] = "FSH.Starter.Migrations.PostgreSQL",
            ["JwtOptions:Issuer"] = TestConstants.JwtIssuer,
            ["JwtOptions:Audience"] = TestConstants.JwtAudience,
            ["JwtOptions:SigningKey"] = TestConstants.JwtSigningKey,
            ["JwtOptions:AccessTokenMinutes"] = "30",
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
            ["HangfireOptions:Password"] = "integration-test-hangfire-pwd",
            ["HangfireOptions:Route"] = "/jobs",
            ["RateLimitingOptions:Enabled"] = "false",
            ["PasswordPolicy:EnforcePasswordExpiry"] = "false",
            ["Seed:DemoPassword"] = "Password123!",
            ["Seed:DefaultAdminPassword"] = TestConstants.DefaultPassword,
            ["SecurityHeadersOptions:Enabled"] = "false",
        }));

        builder.ConfigureServices(services =>
        {
            // Same removals as the shared harness; ReconstrucaoLicencas and RenovacaoPelaConexao stay, they are
            // what the Valkey return exercises.
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

        // ModuleLoader keeps the modules in static state; the shared host already loaded them in this process,
        // and without the reset AddModules would return early and register no module service here.
        var tipo = typeof(ModuleLoader);
        if (tipo.GetField("_modules", BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null) is System.Collections.IList modulos)
        {
            modulos.Clear();
        }

        tipo.GetField("_modulesLoaded", BindingFlags.Static | BindingFlags.NonPublic)?.SetValue(null, false);
        return base.CreateHost(builder);
    }

    // Root tenant, every module migration and seed, as the shared harness does.
    private async Task ProvisionarRaizAsync()
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
        foreach (var init in scope.ServiceProvider.GetServices<IDbInitializer>())
        {
            await init.MigrateAsync(CancellationToken.None);
        }

        foreach (var init in scope.ServiceProvider.GetServices<IDbInitializer>())
        {
            await init.SeedAsync(CancellationToken.None);
        }

        await scope.ServiceProvider.GetRequiredService<FSH.Modules.Identity.Authorization.RolePermissionSyncer>().SyncAsync(CancellationToken.None);
    }
}

/// <summary>
/// Coleção própria e fora do paralelismo: o host lê o Valkey de uma variável de ambiente durante a construção e
/// reinicia o estado estático do ModuleLoader, então nenhum outro host pode ser construído ao mesmo tempo; e
/// pausar os contêineres não pode atingir testes de outra coleção.
/// </summary>
[CollectionDefinition(Nome, DisableParallelization = true)]
public sealed class PafHostValkeyColecao : ICollectionFixture<PafHostValkey>
{
    public const string Nome = "PafHostValkey";
}
