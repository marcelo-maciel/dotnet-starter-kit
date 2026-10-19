using System.Reflection;
using System.Text.RegularExpressions;
using Finbuckle.MultiTenant;
using Finbuckle.MultiTenant.Abstractions;
using FSH.Framework.Jobs.Services;
using FSH.Framework.Mailing.Services;
using FSH.Framework.Persistence;
using FSH.Framework.Shared.Multitenancy;
using FSH.Framework.Web.Modules;
using FSH.Framework.Web.RateLimiting;
using FSH.Modules.Multitenancy.Data;
using Hangfire;
using Hangfire.InMemory;
using Integration.Tests.Infrastructure;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Testcontainers.PostgreSql;

namespace Integration.Tests.Tests.PAF;

/// <summary>
/// Host da API com o limite de taxa e o proxy confiável de produção (O12, ARQ l.380 e l.382): as seções
/// <c>RateLimitingOptions</c> e <c>TrustedProxyOptions</c> saem de <c>appsettings.Production.json</c> com as linhas do
/// arquivo de ambiente <c>deploy/paf/ambientes/producao.env.exemplo</c> por cima, como o contêiner as recebe. O host
/// compartilhado desliga o limitador e confia num proxy fixo posto à mão, então nenhum dos dois caminhos de produção
/// roda lá.
/// </summary>
public sealed partial class PafHostLimiteTaxa : WebApplicationFactory<Program>, IAsyncLifetime
{
    private static readonly string[] SecoesDeProducao = ["RateLimitingOptions", "TrustedProxyOptions"];

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine")
        .WithDatabase("fsh_paf_limite_taxa")
        .WithUsername("postgres")
        .WithPassword("integration_test_pwd")
        .WithAutoRemove(true)
        .WithCleanUp(true)
        .Build();

    private readonly Dictionary<string, string> _configuracaoDeProducao = LerConfiguracaoDeProducao();

    /// <summary>Limites que o host está aplicando, lidos das opções já ligadas (não do arquivo).</summary>
    public RateLimitingOptions Limites => Services.GetRequiredService<IOptions<RateLimitingOptions>>().Value;

    /// <summary>A rede do Docker de produção (<c>PAF_SUBNET</c> do arquivo de ambiente), de onde o Apache chega.</summary>
    public string RedeConfiavel => _configuracaoDeProducao["TrustedProxyOptions:KnownNetworks:0"];

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();

        // The kit binds both sections while the services are registered (AddHeroRateLimiting, the forwarded
        // headers setup), before the factory's configuration overlay applies; environment variables are already
        // there, exactly as in the container. The collection runs alone (DisableParallelization), so no other host
        // is built while they are set.
        var variaveis = _configuracaoDeProducao.Keys.Select(k => k.Replace(":", "__", StringComparison.Ordinal)).ToList();
        foreach (var (chave, valor) in _configuracaoDeProducao)
        {
            Environment.SetEnvironmentVariable(chave.Replace(":", "__", StringComparison.Ordinal), valor);
        }

        try
        {
            _ = Server;
        }
        finally
        {
            foreach (var variavel in variaveis)
            {
                Environment.SetEnvironmentVariable(variavel, null);
            }
        }

        await ProvisionarRaizAsync();
    }

    public new async Task DisposeAsync()
    {
        await base.DisposeAsync();
        await _postgres.DisposeAsync();
    }

    protected override void ConfigureClient(HttpClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        client.DefaultRequestHeaders.Add("Origin", "http://localhost");
        base.ConfigureClient(client);
    }

    // The subset of FshWebApplicationFactory this host needs (no S3, no Valkey: the limiter is in memory).
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DatabaseOptions:Provider"] = "POSTGRESQL",
            ["DatabaseOptions:ConnectionString"] = $"{_postgres.GetConnectionString()};Timeout=15;Command Timeout=30",
            ["DatabaseOptions:MigrationsAssembly"] = "FSH.Starter.Migrations.PostgreSQL",
            ["CachingOptions:Redis"] = "",
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
            ["PasswordPolicy:EnforcePasswordExpiry"] = "false",
            ["Seed:DemoPassword"] = "Password123!",
            ["Seed:DefaultAdminPassword"] = TestConstants.DefaultPassword,
            ["SecurityHeadersOptions:Enabled"] = "false",
        }));

        builder.ConfigureServices(services =>
        {
            // TestServer has no socket: the connection IP comes from X-Test-Remote-Ip, stamped before
            // UseForwardedHeaders, so a test presents itself as the Apache inside the Docker network or as anyone else.
            services.AddSingleton<IStartupFilter, TestRemoteIpStartupFilter>();

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

        // ModuleLoader keeps the modules in static state; another host may already have loaded them in this process.
        var tipo = typeof(ModuleLoader);
        if (tipo.GetField("_modules", BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null) is System.Collections.IList modulos)
        {
            modulos.Clear();
        }

        tipo.GetField("_modulesLoaded", BindingFlags.Static | BindingFlags.NonPublic)?.SetValue(null, false);
        return base.CreateHost(builder);
    }

    /// <summary>
    /// <c>appsettings.Production.json</c> and then the env file's lines, restricted to the two sections, with the
    /// file's own <c>${VAR}</c> references resolved as compose does. A section the file does not set keeps the
    /// production appsettings value.
    /// </summary>
    private static Dictionary<string, string> LerConfiguracaoDeProducao()
    {
        var raiz = RaizDoRepositorio();
        var linhas = File.ReadAllLines(Path.Combine(raiz, "deploy", "paf", "ambientes", "producao.env.exemplo"))
            .Where(l => !l.StartsWith('#') && l.Contains('=', StringComparison.Ordinal))
            .Select(l => l.Split('=', 2))
            .ToDictionary(p => p[0].Trim(), p => p[1].Trim(), StringComparer.Ordinal);

        var configuracao = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(raiz, "src", "Host", "FSH.Starter.Api", "appsettings.Production.json"), optional: false, reloadOnChange: false)
            .AddInMemoryCollection(linhas.ToDictionary(
                p => p.Key.Replace("__", ":", StringComparison.Ordinal),
                p => (string?)Referencia().Replace(p.Value, m => linhas[m.Groups[1].Value]),
                StringComparer.Ordinal))
            .Build();

        var resultado = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var secao in SecoesDeProducao)
        {
            foreach (var item in configuracao.GetSection(secao).AsEnumerable().Where(i => i.Value is not null))
            {
                resultado[item.Key] = item.Value!;
            }
        }

        resultado.ShouldContainKey("RateLimitingOptions:Enabled");
        resultado.ShouldContainKey("TrustedProxyOptions:KnownNetworks:0");
        return resultado;
    }

    private static string RaizDoRepositorio()
    {
        for (var pasta = new DirectoryInfo(AppContext.BaseDirectory); pasta is not null; pasta = pasta.Parent)
        {
            if (File.Exists(Path.Combine(pasta.FullName, "deploy", "paf", "ambientes", "producao.env.exemplo")))
            {
                return pasta.FullName;
            }
        }

        throw new InvalidOperationException($"deploy/paf/ambientes/producao.env.exemplo não encontrado acima de {AppContext.BaseDirectory}");
    }

    [GeneratedRegex(@"\$\{([A-Z0-9_]+)\}")]
    private static partial Regex Referencia();

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
/// Coleção própria e fora do paralelismo: o host lê o limite de taxa e o proxy de variáveis de ambiente durante a
/// construção e reinicia o estado estático do ModuleLoader, então nenhum outro host pode ser construído ao mesmo tempo.
/// </summary>
[CollectionDefinition(Nome, DisableParallelization = true)]
public sealed class PafHostLimiteTaxaColecao : ICollectionFixture<PafHostLimiteTaxa>
{
    public const string Nome = "PafHostLimiteTaxa";
}
