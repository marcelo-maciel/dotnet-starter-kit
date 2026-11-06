using System.Collections.Concurrent;
using System.Net.WebSockets;
using FSH.Modules.PAF.Contracts.v1.TempoReal;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;

namespace PAF.Kestrel.Tests;

/// <summary>One WebSocket upgrade attempt: the token it carried and the HTTP status the server answered (101 = upgraded).</summary>
internal sealed record TentativaUpgrade(string Token, HttpStatusCode Status);

/// <summary>
/// PafHub client configured as the front's <c>paf-hub.ts</c>: WebSockets only, <c>SkipNegotiation</c>, tenant in
/// <c>?tenant=</c> and the token in <c>?access_token=</c> (what a browser sends, so the kit's query-token route list is
/// exercised, never an Authorization header), token read again on every attempt, automatic reconnect. The socket is
/// opened here, so every upgrade attempt and its HTTP status are recorded.
/// </summary>
internal sealed class ConexaoWebSocket : IAsyncDisposable
{
    // Short so a refused reconnect ends in Closed within the test's wait.
    private static readonly TimeSpan[] EsperasReconexao = [TimeSpan.Zero, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)];
    private static readonly TimeSpan PrazoUpgrade = TimeSpan.FromSeconds(10);

    private readonly ConcurrentQueue<TentativaUpgrade> _tentativas = new();

    public ConexaoWebSocket(Uri endereco, string tenant, Func<Task<string>> obterToken)
    {
        var url = new Uri(endereco, $"{PafHubRota.Caminho}?tenant={Uri.EscapeDataString(tenant)}");
        Hub = new HubConnectionBuilder()
            .WithUrl(url, options =>
            {
                options.Transports = HttpTransportType.WebSockets;
                options.SkipNegotiation = true;
                options.WebSocketFactory = (contexto, ct) => AbrirAsync(contexto.Uri, obterToken, ct);
            })
            .WithAutomaticReconnect(EsperasReconexao)
            .Build();
    }

    public HubConnection Hub { get; }

    public IReadOnlyList<TentativaUpgrade> Tentativas => _tentativas.ToArray();

    /// <summary>Completes on the first Reconnecting or Closed: the connection left Connected.</summary>
    public Task<DateTimeOffset> Queda()
    {
        var caiu = new TaskCompletionSource<DateTimeOffset>(TaskCreationOptions.RunContinuationsAsynchronously);
        Hub.Reconnecting += _ =>
        {
            caiu.TrySetResult(DateTimeOffset.UtcNow);
            return Task.CompletedTask;
        };
        Hub.Closed += _ =>
        {
            caiu.TrySetResult(DateTimeOffset.UtcNow);
            return Task.CompletedTask;
        };
        return caiu.Task;
    }

    public Task Reconexao()
    {
        var voltou = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Hub.Reconnected += _ =>
        {
            voltou.TrySetResult();
            return Task.CompletedTask;
        };
        return voltou.Task;
    }

    public Task Fechamento()
    {
        var fechou = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Hub.Closed += _ =>
        {
            fechou.TrySetResult();
            return Task.CompletedTask;
        };
        return fechou.Task;
    }

    /// <summary>
    /// StartAsync returns at the handshake, before PafHubFilter and PafHub.OnConnectedAsync have joined the groups;
    /// the server reads invocations only after that, so one round trip means the groups are in place. A station's
    /// EntrarNoGuiche answers with a HubException, which is the round trip too (same as Integration.Tests).
    /// </summary>
    public async Task ProntaAsync()
    {
        var erro = await Record.ExceptionAsync(() => Hub.InvokeAsync<Guid?>(PafHubChamadas.EntrarNoGuiche));
        if (erro is not null and not HubException)
        {
            throw erro;
        }
    }

    public ValueTask DisposeAsync() => Hub.DisposeAsync();

    private async ValueTask<WebSocket> AbrirAsync(Uri uri, Func<Task<string>> obterToken, CancellationToken ct)
    {
        var token = await obterToken();
        var alvo = new UriBuilder(uri) { Query = $"{uri.Query.TrimStart('?')}&access_token={Uri.EscapeDataString(token)}" }.Uri;
        var socket = new ClientWebSocket();
        socket.Options.CollectHttpResponseDetails = true;
        using var prazo = CancellationTokenSource.CreateLinkedTokenSource(ct);
        prazo.CancelAfter(PrazoUpgrade);
        try
        {
            await socket.ConnectAsync(alvo, prazo.Token);
        }
        catch (WebSocketException)
        {
            // Refused upgrade: record the server's status, then let SignalR see the failure as the browser would.
            _tentativas.Enqueue(new TentativaUpgrade(token, socket.HttpStatusCode));
            socket.Dispose();
            throw;
        }

        _tentativas.Enqueue(new TentativaUpgrade(token, socket.HttpStatusCode));
        return socket;
    }
}
