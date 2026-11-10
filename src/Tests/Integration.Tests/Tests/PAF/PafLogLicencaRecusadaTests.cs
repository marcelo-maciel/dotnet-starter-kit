using System.Diagnostics;
using System.Text;
using FSH.Modules.PAF.TempoReal;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace Integration.Tests.Tests.PAF;

/// <summary>
/// A licence refused when a hub connection opens is expected (the exception's message is the close reason the screen
/// shows, and PafHubFilter logs the refusal as event 9006), but SignalR logs every exception out of OnConnectedAsync
/// as an Error. Through the host's real Serilog pipeline, written to a file: SignalR's Error for that refusal does not
/// reach the sink, and the same Error with any other exception still does. The refusal itself only happens in the
/// race between two instances (with one, the middleware refuses first), so the test writes the event SignalR writes.
/// </summary>
[Collection(PafHostValkeyColecao.Nome)]
public sealed class PafLogLicencaRecusadaTests(PafHostValkey host)
{
    private const string CategoriaDoSignalR = "Microsoft.AspNetCore.SignalR.HubConnectionHandler";
    private static readonly EventId ErroAoDespacharEvento = new(1, "ErrorDispatchingHubEvent");
    private static readonly TimeSpan EsperaDoLog = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task ErroDoSignalRNaAbertura_ComLicencaRecusada_NaoChegaAoLog_EComOutraExcecao_Chega()
    {
        string marca = Guid.NewGuid().ToString("N");
        long inicio = TamanhoDoLog();
        var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger(CategoriaDoSignalR);

        logger.LogError(ErroAoDespacharEvento, new PafHubFilter.LicencaRecusadaException($"licenca {marca}"),
            "Error when dispatching '{HubMethod}' on hub.", "OnConnectedAsync");
        logger.LogError(ErroAoDespacharEvento, new HubException($"outra {marca}"),
            "Error when dispatching '{HubMethod}' on hub.", "OnConnectedAsync");

        // The sink writes in order: once the second event is in the file, the first one would be too.
        string novo = await EsperarAsync(inicio, $"outra {marca}");

        novo.ShouldNotContain($"licenca {marca}");
    }

    private long TamanhoDoLog() => File.Exists(host.ArquivoDeLog) ? new FileInfo(host.ArquivoDeLog).Length : 0;

    private string LerLogDesde(long inicio)
    {
        if (!File.Exists(host.ArquivoDeLog))
        {
            return string.Empty;
        }

        // The sink holds the file open for writing; share it instead of copying.
        using var arquivo = new FileStream(host.ArquivoDeLog, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        arquivo.Seek(inicio, SeekOrigin.Begin);
        using var leitor = new StreamReader(arquivo, Encoding.UTF8);
        return leitor.ReadToEnd();
    }

    private async Task<string> EsperarAsync(long inicio, string trecho)
    {
        var relogio = Stopwatch.StartNew();
        while (true)
        {
            string novo = LerLogDesde(inicio);
            if (novo.Contains(trecho, StringComparison.Ordinal))
            {
                return novo;
            }

            if (relogio.Elapsed > EsperaDoLog)
            {
                throw new ShouldAssertException($"\"{trecho}\" não apareceu em {EsperaDoLog.TotalSeconds} s no log {host.ArquivoDeLog}");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(200));
        }
    }
}
