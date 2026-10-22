using System.Diagnostics;
using System.Net.Sockets;
using System.Threading.Channels;

namespace PAF.Latencia.Tests;

/// <summary>
/// TCP relay between the hosts and the Valkey container that holds every chunk coming back from Valkey for
/// <see cref="Atraso"/> before forwarding it (the "latency" toxic of Toxiproxy, downstream). Each chunk keeps its own
/// deadline, so the delay is added once per reply or pub/sub message, never accumulated over a pipelined stream.
/// Every connection (interactive, subscriber, SignalR backplane) goes through it.
/// </summary>
internal sealed class RetransmissorComAtraso : IAsyncDisposable
{
    private static readonly TimeSpan PrazoConexao = TimeSpan.FromSeconds(5);

    private readonly TcpListener _ouvinte = new(IPAddress.Loopback, 0);
    private readonly string _hostDestino;
    private readonly int _portaDestino;
    private readonly CancellationTokenSource _fim = new();
    private readonly List<Task> _pares = [];
    private Task _aceitacao = Task.CompletedTask;
    private long _atrasoTicks;

    public RetransmissorComAtraso(string hostDestino, int portaDestino)
    {
        _hostDestino = hostDestino;
        _portaDestino = portaDestino;
    }

    /// <summary>Delay applied to the chunks read from Valkey from now on.</summary>
    public TimeSpan Atraso
    {
        get => TimeSpan.FromTicks(Interlocked.Read(ref _atrasoTicks));
        set => Interlocked.Exchange(ref _atrasoTicks, value.Ticks);
    }

    public int Porta { get; private set; }

    public void Iniciar()
    {
        _ouvinte.Start();
        Porta = ((IPEndPoint)_ouvinte.LocalEndpoint).Port;
        _aceitacao = AceitarAsync(_fim.Token);
    }

    public async ValueTask DisposeAsync()
    {
        await _fim.CancelAsync();
        _ouvinte.Dispose();
        await _aceitacao;
        Task[] pares;
        lock (_pares)
        {
            pares = [.. _pares];
        }

        await Task.WhenAll(pares);
        _fim.Dispose();
    }

    private async Task AceitarAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient cliente;
            try
            {
                cliente = await _ouvinte.AcceptTcpClientAsync(ct);
            }
            catch (OperationCanceledException)
            {
                // Dispose stopped the listener: no more connections to accept.
                return;
            }

            var par = RetransmitirAsync(cliente, ct);
            lock (_pares)
            {
                _pares.Add(par);
            }
        }
    }

    private async Task RetransmitirAsync(TcpClient cliente, CancellationToken ct)
    {
        using (cliente)
        using (var destino = new TcpClient())
        using (var fimDoPar = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            using (var prazo = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                prazo.CancelAfter(PrazoConexao);
                await destino.ConnectAsync(_hostDestino, _portaDestino, prazo.Token);
            }

            cliente.NoDelay = true;
            destino.NoDelay = true;
            var ida = BombearAsync(cliente.GetStream(), destino.GetStream(), () => TimeSpan.Zero, fimDoPar.Token);
            var volta = BombearAsync(destino.GetStream(), cliente.GetStream(), () => Atraso, fimDoPar.Token);

            // Either side closing ends the pair: the other pump is cancelled and both sockets are closed.
            await Task.WhenAny(ida, volta);
            await fimDoPar.CancelAsync();
            await Task.WhenAll(ida, volta);
        }
    }

    private static async Task BombearAsync(NetworkStream origem, NetworkStream destino, Func<TimeSpan> atrasoAtual, CancellationToken ct)
    {
        var fila = Channel.CreateUnbounded<(byte[] Dados, long Liberacao)>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
        var escrita = EscreverAsync(fila.Reader, destino, ct);
        var buffer = new byte[64 * 1024];
        try
        {
            while (true)
            {
                var lidos = await origem.ReadAsync(buffer, ct);
                if (lidos == 0)
                {
                    break;
                }

                var atraso = atrasoAtual();
                fila.Writer.TryWrite((buffer[..lidos], Stopwatch.GetTimestamp() + (long)(atraso.TotalSeconds * Stopwatch.Frequency)));
            }
        }
        catch (Exception erro) when (erro is IOException or OperationCanceledException or ObjectDisposedException)
        {
            // The peer or the pair closed: this direction is over, which the caller turns into closing the pair.
        }
        finally
        {
            fila.Writer.TryComplete();
        }

        await escrita;
    }

    private static async Task EscreverAsync(ChannelReader<(byte[] Dados, long Liberacao)> fila, NetworkStream destino, CancellationToken ct)
    {
        try
        {
            await foreach (var (dados, liberacao) in fila.ReadAllAsync(ct))
            {
                var falta = Stopwatch.GetElapsedTime(Stopwatch.GetTimestamp(), liberacao);
                if (falta > TimeSpan.Zero)
                {
                    await Task.Delay(falta, ct);
                }

                await destino.WriteAsync(dados, ct);
            }
        }
        catch (Exception erro) when (erro is IOException or OperationCanceledException or ObjectDisposedException)
        {
            // Same as the read side: a closed peer or pair ends this direction.
        }
    }
}
