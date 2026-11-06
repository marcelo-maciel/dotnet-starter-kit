using Amazon.Runtime;
using Amazon.S3;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace PAF.Tests.PrazosEntradaSaida;

/// <summary>
/// RNF05 barrier, configuration half (O19): the values the API host resolves for PostgreSQL, Valkey, named
/// HttpClients and AWS clients carry an explicit, finite deadline under <see cref="RegrasDePrazo.Teto"/>.
/// The behaviour with a paused dependency is tested by S03, S09, S14 and M03.
/// </summary>
// Serial with every class that reads process environment variables: one test here writes one.
[Collection(nameof(VariaveisDeAmbienteDoProcesso))]
public sealed class PrazosConfiguracaoTests
{
    private const string VariavelDoBanco = "DatabaseOptions__ConnectionString";

    [Fact]
    public void ConfiguracaoDeDesenvolvimento_Should_TerPrazoExplicitoEmTodaEntradaSaida()
    {
        var avaliacao = PrazosDoHost.Avaliar(PrazosDoHost.CriarBuilder("Development"));

        // Positive control: appsettings.json carries the development connection string, so an evaluation that
        // checked nothing would be a broken composition and not a compliant one.
        avaliacao.Conferidos.ShouldContain(PrazosDoHost.ChaveBanco);
        avaliacao.Violacoes.ShouldBeEmpty(string.Join(Environment.NewLine, avaliacao.Violacoes));
    }

    [Fact]
    public void ConfiguracaoDeProducao_Should_TerPrazoExplicito_OuRecusarABaseSemConfiguracao()
    {
        var builder = PrazosDoHost.CriarBuilder("Production");

        // The production file leaves the connection string to the environment. Without the variable the kit's
        // option validation refuses to hand out a connection string, so there is no fallback without a deadline.
        // With the variable set, as in a deploy or a CI shell, the resolved value is what gets checked.
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(VariavelDoBanco)))
        {
            Should.Throw<Microsoft.Extensions.Options.OptionsValidationException>(() => PrazosDoHost.Avaliar(builder))
                .Message.ShouldContain("ConnectionString");
            return;
        }

        var avaliacao = PrazosDoHost.Avaliar(builder);
        avaliacao.Violacoes.ShouldBeEmpty(string.Join(Environment.NewLine, avaliacao.Violacoes));
    }

    [Fact]
    public void VariavelDeAmbiente_Should_Reprovar_When_StringDoBancoNaoTemCommandTimeout()
    {
        const string variavel = VariavelDoBanco;
        string? anterior = Environment.GetEnvironmentVariable(variavel);
        Environment.SetEnvironmentVariable(variavel, "Host=db;Database=paf;Timeout=5");
        try
        {
            var avaliacao = PrazosDoHost.Avaliar(PrazosDoHost.CriarBuilder("Production"));

            avaliacao.Violacoes.ShouldHaveSingleItem().ShouldContain("'Command Timeout' ausente");
        }
        finally
        {
            Environment.SetEnvironmentVariable(variavel, anterior);
        }
    }

    [Theory]
    [InlineData("Host=db;Timeout=5", "'Command Timeout' ausente")]
    [InlineData("Host=db;Command Timeout=10", "'Timeout' ausente")]
    [InlineData("Host=db;Timeout=5;Command Timeout=0", "Command Timeout infinito")]
    [InlineData("Host=db;Timeout=5;Command Timeout=31", "acima do teto")]
    public void StringDoBanco_Should_Reprovar_When_PrazoAusenteInfinitoOuAcimaDoTeto(string conexao, string esperado)
    {
        var avaliacao = AvaliarComConfiguracao(PrazosDoHost.ChaveBanco, conexao);

        avaliacao.Violacoes.ShouldHaveSingleItem().ShouldContain(esperado);
    }

    [Fact]
    public void StringDoBanco_Should_Aprovar_When_PrazosExplicitosNoTeto()
    {
        var avaliacao = AvaliarComConfiguracao(PrazosDoHost.ChaveBanco, "Host=db;timeout=30;command timeout=30");

        avaliacao.Violacoes.ShouldBeEmpty();
        avaliacao.Conferidos.ShouldContain(PrazosDoHost.ChaveBanco);
    }

    [Theory]
    [InlineData("valkey:6379,connectTimeout=2000,asyncTimeout=2000", "'syncTimeout' ausente")]
    [InlineData("valkey:6379,syncTimeout=2000,asyncTimeout=2000", "'connectTimeout' ausente")]
    [InlineData("valkey:6379,connectTimeout=2000,syncTimeout=2000", "'asyncTimeout' ausente")]
    [InlineData("valkey:6379,connectTimeout=2000,syncTimeout=31000,asyncTimeout=2000", "syncTimeout = 00:00:31 acima do teto")]
    [InlineData("valkey:6379,connectTimeout=60000,syncTimeout=2000,asyncTimeout=2000", "connectTimeout = 00:01:00 acima do teto")]
    public void ConfiguracaoDoValkey_Should_Reprovar_When_PrazoAusenteOuAcimaDoTeto(string valkey, string esperado)
    {
        var avaliacao = AvaliarComConfiguracao(PrazosDoHost.ChaveValkey, valkey);

        avaliacao.Violacoes.ShouldHaveSingleItem().ShouldContain(esperado);
    }

    [Fact]
    public void ConfiguracaoDoValkey_Should_Aprovar_When_OsTresPrazosExplicitos()
    {
        var avaliacao = AvaliarComConfiguracao(
            PrazosDoHost.ChaveValkey, "valkey:6379,CONNECTTIMEOUT=2000,syncTimeout=1000,asyncTimeout=1000");

        avaliacao.Violacoes.ShouldBeEmpty();
        avaliacao.Conferidos.ShouldContain(PrazosDoHost.ChaveValkey);
    }

    [Fact]
    public void HttpClientNomeado_Should_Reprovar_When_TimeoutInfinito()
    {
        var builder = PrazosDoHost.CriarBuilder("Development");
        builder.Services.AddHttpClient("sem-prazo", c => c.Timeout = Timeout.InfiniteTimeSpan);

        var avaliacao = PrazosDoHost.Avaliar(builder);

        avaliacao.Violacoes.ShouldHaveSingleItem().ShouldContain("HttpClient 'sem-prazo'");
    }


    [Fact]
    public void HttpClientNomeado_Should_Reprovar_When_FicaNoPadraoDoFramework()
    {
        // The framework default (100 s) is finite but not explicit, and the ceiling sits below it on purpose.
        var builder = PrazosDoHost.CriarBuilder("Development");
        builder.Services.AddHttpClient("padrao").ConfigureHttpClient(c => c.DefaultRequestVersion = new Version(1, 1));

        var avaliacao = PrazosDoHost.Avaliar(builder);

        avaliacao.Violacoes.ShouldHaveSingleItem().ShouldContain("HttpClient 'padrao': Timeout = 00:01:40 acima do teto");
    }

    [Fact]
    public void HttpClientNomeado_Should_Aprovar_When_TimeoutExplicitoNoTeto()
    {
        var builder = PrazosDoHost.CriarBuilder("Development");
        builder.Services.AddHttpClient("com-prazo", c => c.Timeout = TimeSpan.FromSeconds(10));

        var avaliacao = PrazosDoHost.Avaliar(builder);

        avaliacao.Violacoes.ShouldBeEmpty();
        avaliacao.Conferidos.ShouldContain("HttpClient 'com-prazo'");
    }

    [Fact]
    public void ClienteS3_Should_Reprovar_When_ConfiguracaoSemTimeout()
    {
        var builder = PrazosDoHost.CriarBuilder("Development");
        builder.Services.AddSingleton<IAmazonS3>(_ => ClienteS3(new AmazonS3Config { ServiceURL = "http://s3.invalid" }));

        var avaliacao = PrazosDoHost.Avaliar(builder);

        avaliacao.Violacoes.ShouldHaveSingleItem().ShouldContain("IAmazonS3[]: Timeout = 24.20:31:23.6470000 acima do teto");
    }

    [Fact]
    public void ClienteS3Chaveado_Should_Reprovar_When_TimeoutAcimaDoTeto()
    {
        var builder = PrazosDoHost.CriarBuilder("Development");
        builder.Services.AddKeyedSingleton<IAmazonS3>("assinatura", (_, _) => ClienteS3(new AmazonS3Config
        {
            ServiceURL = "http://s3.invalid",
            Timeout = TimeSpan.FromMinutes(2),
        }));

        var avaliacao = PrazosDoHost.Avaliar(builder);

        avaliacao.Violacoes.ShouldHaveSingleItem().ShouldContain("IAmazonS3[assinatura]: Timeout = 00:02:00 acima do teto");
    }

    [Fact]
    public void ClienteS3_Should_Aprovar_When_TimeoutExplicitoNoTeto()
    {
        var builder = PrazosDoHost.CriarBuilder("Development");
        builder.Services.AddSingleton<IAmazonS3>(_ => ClienteS3(new AmazonS3Config
        {
            ServiceURL = "http://s3.invalid",
            Timeout = TimeSpan.FromSeconds(20),
        }));

        var avaliacao = PrazosDoHost.Avaliar(builder);

        avaliacao.Violacoes.ShouldBeEmpty();
        avaliacao.Conferidos.ShouldContain("IAmazonS3[]");
    }

    private static AvaliacaoDePrazos AvaliarComConfiguracao(string chave, string valor)
    {
        var builder = PrazosDoHost.CriarBuilder("Development");
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { [chave] = valor });
        return PrazosDoHost.Avaliar(builder);
    }

    // Credentials only so the SDK never looks for ambient ones; the client is built, never called.
    private static AmazonS3Client ClienteS3(AmazonS3Config configuracao) =>
        new(new BasicAWSCredentials("o19", "o19"), configuracao);
}

[CollectionDefinition(nameof(VariaveisDeAmbienteDoProcesso), DisableParallelization = true)]
public sealed class VariaveisDeAmbienteDoProcesso;
