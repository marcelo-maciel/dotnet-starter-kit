using System.Diagnostics;
using System.Text;
using FSH.Modules.PAF.TempoReal;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace Integration.Tests.Tests.PAF;

/// <summary>
/// The PafHub refuses a connection or a call on purpose, with a licence refused on re-occupation or a credential or
/// session already ended: the exception's message is the cause the screen shows, and PafHubFilter logs the refusal
/// itself (events 9006 and 9003). SignalR logs every exception out of OnConnectedAsync and out of a hub method as an
/// Error. Through the host's real Serilog pipeline, written to a file: SignalR's Error for those two refusals does not
/// reach the sink, from either SignalR source, and the same Error with any other exception still does. The refusals
/// themselves only happen in the race between two instances (with one, the middleware refuses first), so the test
/// writes the events SignalR writes.
/// </summary>
[Collection(PafHostValkeyColecao.Nome)]
public sealed class PafLogRecusaDoHubTests(PafHostValkey host)
{
    private const string AberturaDoSignalR = "Microsoft.AspNetCore.SignalR.HubConnectionHandler";
    private const string ChamadaDoSignalR = "Microsoft.AspNetCore.SignalR.Internal.DefaultHubDispatcher";
    private static readonly EventId ErroAoDespacharEvento = new(1, "ErrorDispatchingHubEvent");
    private static readonly EventId FalhaAoChamarMetodo = new(8, "FailedInvokingHubMethod");
    private static readonly TimeSpan EsperaDoLog = TimeSpan.FromSeconds(10);

    [Theory]
    [InlineData(AberturaDoSignalR, "licenca")]
    [InlineData(AberturaDoSignalR, "credencial")]
    [InlineData(ChamadaDoSignalR, "licenca")]
    [InlineData(ChamadaDoSignalR, "credencial")]
    public async Task ErroDoSignalR_ComRecusaDoPaf_NaoChegaAoLog_EComOutraExcecao_Chega(string categoria, string recusa)
    {
        string marca = Guid.NewGuid().ToString("N");
        long inicio = TamanhoDoLog();
        var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger(categoria);
        var evento = categoria == AberturaDoSignalR ? ErroAoDespacharEvento : FalhaAoChamarMetodo;
        HubException recusada = recusa == "licenca"
            ? new PafHubFilter.LicencaRecusadaException($"recusa {marca}")
            : new PafHubFilter.CredencialRecusadaException($"recusa {marca}");

        logger.LogError(evento, recusada, "Error when dispatching '{HubMethod}' on hub.", "OnConnectedAsync");
        logger.LogError(evento, new HubException($"outra {marca}"), "Error when dispatching '{HubMethod}' on hub.", "OnConnectedAsync");

        // The sink writes in order: once the second event is in the file, the first one would be too.
        string novo = await EsperarAsync(inicio, $"outra {marca}");

        novo.ShouldNotContain($"recusa {marca}");
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
