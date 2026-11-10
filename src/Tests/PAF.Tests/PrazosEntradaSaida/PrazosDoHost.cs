using Amazon.Runtime;
using FSH.Framework.Caching;
using FSH.Framework.Persistence;
using FSH.Framework.Shared.Persistence;
using FSH.Modules.PAF;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Options;

namespace PAF.Tests.PrazosEntradaSaida;

internal sealed record AvaliacaoDePrazos(List<string> Violacoes, List<string> Conferidos, List<string> Ausentes);

/// <summary>
/// Composes the API host configuration from the same sources the host reads (its appsettings files, copied next to
/// the tests, the environment overlay and the process environment variables), registers the PAF module on it and
/// evaluates the values the clients would be built from. Nothing is started and nothing connects.
/// </summary>
internal static class PrazosDoHost
{
    internal const string ChaveBanco = "DatabaseOptions:ConnectionString";
    internal const string ChaveValkey = "CachingOptions:Redis";

    internal static HostApplicationBuilder CriarBuilder(string ambiente)
    {
        string raiz = Path.Combine(AppContext.BaseDirectory, "host");
        File.Exists(Path.Combine(raiz, "appsettings.json")).ShouldBeTrue(
            $"appsettings.json da API ausente em {raiz}; a avaliação passaria sobre configuração vazia");

        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            EnvironmentName = ambiente,
            ContentRootPath = raiz,
            Args = [],
        });

        // The same option bindings the host uses, so the values read are the bound ones, not raw keys.
        builder.Services.AddHeroDatabaseOptions(builder.Configuration);
        builder.Services.AddOptions<CachingOptions>().BindConfiguration(nameof(CachingOptions));
        builder.Services.AddHttpClient();
        new PafModule().ConfigureServices(builder);
        return builder;
    }

    internal static AvaliacaoDePrazos Avaliar(HostApplicationBuilder builder)
    {
        var avaliacao = new AvaliacaoDePrazos([], [], []);

        // No ValidateOnBuild: the PAF registrations depend on kit services this composition does not add,
        // and only the clients below are ever resolved.
        using var provedor = builder.Services.BuildServiceProvider();

        string banco = provedor.GetRequiredService<IOptions<DatabaseOptions>>().Value.ConnectionString;
        AvaliarValor(avaliacao, ChaveBanco, banco, RegrasDePrazo.Postgres);

        string valkey = provedor.GetRequiredService<IOptions<CachingOptions>>().Value.Redis;
        AvaliarValor(avaliacao, ChaveValkey, valkey, RegrasDePrazo.Valkey);

        var fabrica = provedor.GetRequiredService<IHttpClientFactory>();
        foreach (string nome in NomesDeHttpClient(builder.Services))
        {
            using var cliente = fabrica.CreateClient(nome);
            avaliacao.Conferidos.Add($"HttpClient '{nome}'");
            avaliacao.Violacoes.AddRange(RegrasDePrazo.HttpClientNomeado(nome, cliente.Timeout));
        }

        foreach (var descritor in builder.Services.Where(d => typeof(IAmazonService).IsAssignableFrom(d.ServiceType)))
        {
            var cliente = (IAmazonService)(descritor.IsKeyedService
                ? provedor.GetRequiredKeyedService(descritor.ServiceType, descritor.ServiceKey)
                : provedor.GetRequiredService(descritor.ServiceType));
            string origem = $"{descritor.ServiceType.Name}[{descritor.ServiceKey}]";
            avaliacao.Conferidos.Add(origem);
            avaliacao.Violacoes.AddRange(RegrasDePrazo.ClienteAws(origem, cliente.Config));
        }

        return avaliacao;
    }

    // A named client is visible only through its named HttpClientFactoryOptions configure actions, since the
    // factory creates any name it is asked for. A bare AddHttpClient(name) with no configuration at all leaves no
    // registration behind and is indistinguishable from the default client: that case is out of this barrier's reach.
    private static IEnumerable<string> NomesDeHttpClient(IServiceCollection servicos) =>
        servicos
            .Select(d => d.ImplementationInstance)
            .OfType<ConfigureNamedOptions<HttpClientFactoryOptions>>()
            .Select(o => o.Name)
            .OfType<string>()
            .Distinct(StringComparer.Ordinal);

    private static void AvaliarValor(
        AvaliacaoDePrazos avaliacao,
        string chave,
        string valor,
        Func<string, string, List<string>> regra)
    {
        // Empty means the dependency is not configured in this composition: in Production the API refuses to
        // start without these keys (Program.cs), and the values come from the environment files (O02).
        if (string.IsNullOrWhiteSpace(valor))
        {
            avaliacao.Ausentes.Add(chave);
            return;
        }

        avaliacao.Conferidos.Add(chave);
        avaliacao.Violacoes.AddRange(regra(chave, valor));
    }
}
