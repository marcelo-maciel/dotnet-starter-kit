using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Claims;
using FSH.Framework.Shared.Constants;
using FSH.Framework.Web.Observability.Logging.Serilog;
using FSH.Modules.PAF.Observabilidade;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PAF.Tests.PrazosEntradaSaida;
using Serilog;
using Serilog.Configuration;
using Serilog.Core;
using Serilog.Events;

namespace PAF.Tests.Observabilidade;

/// <summary>
/// O10 (RNF06, ARQ l.392) over the real logging wiring of the host: the API's own appsettings for Production, the
/// <c>Serilog__*</c> lines of the deployment environment file, the kit's <c>AddHeroLogging</c> and the PAF module.
/// Events reach the test through a sink declared in that same configuration (<see cref="CapturaDeLog"/>), so what is
/// asserted is the event after every enricher, as the sinks see it.
/// </summary>
// Serial with every class that reads process environment variables: the OTLP test writes the OTEL_* ones, which
// AddHeroLogging reads from the process.
[Collection(nameof(VariaveisDeAmbienteDoProcesso))]
public sealed class LogDoHostTests
{
    private const string Email = "atendente.o10@cliente.com.br";
    private static readonly TimeSpan TetoPorEvento = TimeSpan.FromMilliseconds(100);

    [Fact]
    public void RequisicaoAutenticada_ComOModuloDoPaf_EmailSaiOmitido_ETenantEUsuarioContinuam()
    {
        var evento = LogarNaRequisicao(PrazosDoHost.CriarBuilder("Production"));

        evento.Properties[EnriquecedorSemDadosPessoais.PropriedadeEmail].ToString().ShouldBe($"\"{EnriquecedorSemDadosPessoais.Omitido}\"");
        // The kit's enricher still ran: only the e-mail was claimed first.
        evento.Properties["Tenant"].ToString().ShouldBe("\"tenant-o10\"");
        evento.Properties["UserId"].ToString().ShouldBe("\"u-o10\"");
    }

    // Control: the same host without the PAF module logs the e-mail, so the test above sees the kit's real behaviour.
    [Fact]
    public void RequisicaoAutenticada_SemOModuloDoPaf_OKitGravaOEmail()
    {
        var evento = LogarNaRequisicao(BuilderSemOPaf());

        evento.Properties[EnriquecedorSemDadosPessoais.PropriedadeEmail].ToString().ShouldBe($"\"{Email}\"");
    }

    [Fact]
    public async Task EndpointOtlpQueAceitaENaoResponde_NenhumLogAtrasaMaisDe100ms()
    {
        using var buraco = new TcpListener(IPAddress.Loopback, 0);
        buraco.Start();
        int porta = ((IPEndPoint)buraco.LocalEndpoint).Port;

        HostApplicationBuilder builder;
        string? endpointAnterior = Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT");
        string? protocoloAnterior = Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_PROTOCOL");
        Environment.SetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT", $"http://127.0.0.1:{porta}");
        Environment.SetEnvironmentVariable("OTEL_EXPORTER_OTLP_PROTOCOL", "http/protobuf");
        try
        {
            builder = PrazosDoHost.CriarBuilder("Production");
            AplicarAmbienteDeImplantacao(builder);
            // Only the OTLP sink and the capture take the events: a console write would be measured instead.
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Serilog:WriteTo:0:Args:restrictedToMinimumLevel"] = "Fatal",
            });
            // The kit resolves the OTLP export from the process environment here, once.
            builder.AddHeroLogging();
        }
        finally
        {
            Environment.SetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT", endpointAnterior);
            Environment.SetEnvironmentVariable("OTEL_EXPORTER_OTLP_PROTOCOL", protocoloAnterior);
        }

        builder.Services.AddHttpContextAccessor();
        using var host = builder.Build();
        var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("O10");
        string marca = Guid.NewGuid().ToString("N");

        // First the exporter must be stuck on the endpoint, else the loop below would measure an idle sink.
        logger.LogWarning("O10 primeiro evento {Marca}", marca);
        using var prazo = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var conexao = await buraco.AcceptTcpClientAsync(prazo.Token);

        var pior = TimeSpan.Zero;
        var relogio = new Stopwatch();
        for (int i = 0; i < 2000; i++)
        {
            relogio.Restart();
            logger.LogWarning("O10 evento {Indice} {Marca}", i, marca);
            if (relogio.Elapsed > pior)
            {
                pior = relogio.Elapsed;
            }
        }

        CapturaDeLog.Eventos.Count(e => e.Properties.TryGetValue("Marca", out var m) && m.ToString() == $"\"{marca}\"")
            .ShouldBe(2001, "todo evento chega aos sinks enquanto o OTLP está preso");
        pior.ShouldBeLessThan(TetoPorEvento);

        // Reset the held connection and the backlog so the final flush fails fast instead of waiting the HTTP timeout.
        conexao.Client.LingerState = new LingerOption(true, 0);
        conexao.Close();
        buraco.Stop();
    }

    private static LogEvent LogarNaRequisicao(HostApplicationBuilder builder)
    {
        AplicarAmbienteDeImplantacao(builder);
        builder.AddHeroLogging();
        builder.Services.AddHttpContextAccessor();
        using var host = builder.Build();
        var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("O10");
        string marca = Guid.NewGuid().ToString("N");

        var acesso = new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                    [
                        new Claim(ClaimTypes.NameIdentifier, "u-o10"),
                        new Claim(ClaimTypes.Email, Email),
                        new Claim(CustomClaims.Tenant, "tenant-o10"),
                    ],
                    authenticationType: "Bearer")),
            },
        };
        try
        {
            logger.LogWarning("O10 requisição autenticada {Marca}", marca);
        }
        finally
        {
            acesso.HttpContext = null;
        }

        return CapturaDeLog.Eventos.Single(e => e.Properties.TryGetValue("Marca", out var m) && m.ToString() == $"\"{marca}\"");
    }

    // Same composition as PrazosDoHost.CriarBuilder, minus the PAF module.
    private static HostApplicationBuilder BuilderSemOPaf() =>
        Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            EnvironmentName = "Production",
            ContentRootPath = Path.Combine(AppContext.BaseDirectory, "host"),
            Args = [],
        });

    // The Serilog lines of the production environment file (deploy/paf/ambientes, copied next to the tests), as the
    // container gets them, plus the capture sink.
    private static void AplicarAmbienteDeImplantacao(HostApplicationBuilder builder)
    {
        string arquivo = Path.Combine(AppContext.BaseDirectory, "ambientes", "producao.env.exemplo");
        File.Exists(arquivo).ShouldBeTrue($"arquivo de ambiente ausente em {arquivo}");
        var linhas = File.ReadAllLines(arquivo)
            .Select(l => l.Trim())
            .Where(l => l.StartsWith("Serilog__", StringComparison.Ordinal))
            .ToDictionary(
                l => l[..l.IndexOf('=', StringComparison.Ordinal)].Replace("__", ":", StringComparison.Ordinal),
                l => (string?)l[(l.IndexOf('=', StringComparison.Ordinal) + 1)..]);
        linhas.ShouldNotBeEmpty();
        linhas["Serilog:Using:91"] = typeof(CapturaDeLog).Assembly.GetName().Name;
        linhas["Serilog:WriteTo:91:Name"] = nameof(CapturaDeLog.CapturaDeTeste);
        builder.Configuration.AddInMemoryCollection(linhas);
    }
}

/// <summary>Sink declared through configuration, so it sits where the host's own sinks sit.</summary>
public static class CapturaDeLog
{
    internal static ConcurrentQueue<LogEvent> Eventos { get; } = new();

    public static LoggerConfiguration CapturaDeTeste(this LoggerSinkConfiguration writeTo)
    {
        ArgumentNullException.ThrowIfNull(writeTo);
        return writeTo.Sink(new Coletor());
    }

    private sealed class Coletor : ILogEventSink
    {
        public void Emit(LogEvent logEvent) => Eventos.Enqueue(logEvent);
    }
}
