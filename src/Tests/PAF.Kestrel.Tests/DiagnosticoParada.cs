using System.Collections.Concurrent;
using System.Diagnostics;
using FSH.Modules.PAF.TempoReal;
using Hangfire;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Hangfire.Storage;
using Microsoft.Extensions.Hosting;

namespace PAF.Kestrel.Tests;

/// <summary>
/// Test-only record of the host's stop, for the CI-only "Test Collection Cleanup Failure (PafKestrel)
/// OperationCanceledException": in the failed runs the stop took 31 s against 0.4 s, the host's 30 s shutdown timeout,
/// and nothing in the log said what held it. The host stops its services one after the other with one shared token, so
/// the service that throws is not necessarily the one that held the stop: this keeps when each BackgroundService ended
/// and, if the stop passes 10 s, what Hangfire was running and how the thread pool looked. Written to the file named by
/// PAF_KESTREL_DIAGNOSTICO (the CI step prints it), or next to the test assembly. It changes nothing in the stop.
/// </summary>
internal sealed class DiagnosticoParada : IDisposable
{
    private static readonly TimeSpan Limiar = TimeSpan.FromSeconds(10);

    private readonly long _inicio = Stopwatch.GetTimestamp();
    private readonly ConcurrentQueue<string> _linhas = new();
    private readonly CancellationTokenSource _fim = new();
    private readonly Task _vigia;

    private readonly ConcurrentDictionary<string, ConexaoAberta> _conexoesKestrel;

    public DiagnosticoParada(IServiceProvider servicos, ConcurrentDictionary<string, ConexaoAberta> conexoesKestrel)
    {
        _conexoesKestrel = conexoesKestrel;
        Anotar($"conexões PafHub ainda registradas: {servicos.GetRequiredService<ConexoesPaf>().Locais().Count}");
        AnotarConexoesKestrel();
        // Singletons: the same instances the host runs (the Hangfire servers are transient and not BackgroundService).
        foreach (var servico in servicos.GetServices<IHostedService>().OfType<BackgroundService>())
        {
            var nome = servico.GetType().Name;
            if (servico.ExecuteTask is { } execucao)
            {
                _ = execucao.ContinueWith(t => Anotar($"{nome}: ExecuteAsync terminou ({t.Status})"), TaskScheduler.Default);
            }
        }

        _vigia = VigiarAsync(servicos.GetRequiredService<JobStorage>().GetMonitoringApi());
    }

    public void Dispose()
    {
        _fim.Cancel();
        _vigia.GetAwaiter().GetResult();
        _fim.Dispose();
        AnotarConexoesKestrel();
        Anotar("parada do host terminou");
        var destino = Environment.GetEnvironmentVariable("PAF_KESTREL_DIAGNOSTICO")
            ?? Path.Combine(AppContext.BaseDirectory, "paf-kestrel-parada.log");
        File.WriteAllLines(destino, _linhas);
    }

    private async Task VigiarAsync(IMonitoringApi hangfire)
    {
        try
        {
            await Task.Delay(Limiar, _fim.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        ThreadPool.GetAvailableThreads(out var livres, out _);
        ThreadPool.GetMaxThreads(out var maximo, out _);
        Anotar($"parada passou de {Limiar.TotalSeconds:0} s; thread pool: {ThreadPool.ThreadCount} threads, "
            + $"{ThreadPool.PendingWorkItemCount} itens pendentes, {maximo - livres} workers ocupados");
        AnotarConexoesKestrel();
        foreach (var servidor in hangfire.Servers())
        {
            Anotar($"  Hangfire servidor {servidor.Name}: {servidor.WorkersCount} workers, filas {string.Join(',', servidor.Queues)}, heartbeat {servidor.Heartbeat:O}");
        }

        foreach (var (id, job) in hangfire.ProcessingJobs(0, 50))
        {
            Anotar($"  Hangfire em execução {id}: {job.Job?.Type.Name}.{job.Job?.Method.Name} no servidor {job.ServerId} desde {job.StartedAt:O}");
        }

        foreach (var fila in hangfire.Queues())
        {
            Anotar($"  Hangfire fila {fila.Name}: {fila.Length} na fila, {fila.Fetched} buscados");
        }
    }

    private void AnotarConexoesKestrel()
    {
        Anotar($"conexões Kestrel abertas: {_conexoesKestrel.Count}");
        foreach (var (id, conexao) in _conexoesKestrel)
        {
            Anotar($"  {id} de {conexao.Remoto} aberta {conexao.AbertaEm:HH:mm:ss.fff}, último pedido {conexao.UltimoPedido ?? "(nenhum)"}");
        }
    }

    private void Anotar(string linha) =>
        _linhas.Enqueue($"[{Stopwatch.GetElapsedTime(_inicio).TotalMilliseconds,8:0} ms] {linha}");
}

/// <summary>A Kestrel connection still open: where from, since when, and the last request it carried (path only).</summary>
internal sealed record ConexaoAberta(string? Remoto, DateTimeOffset AbertaEm, string? UltimoPedido);

/// <summary>Writes each request's method and path on its connection's entry, so an open connection says what it served.</summary>
internal sealed class UltimoPedidoDaConexao(ConcurrentDictionary<string, ConexaoAberta> conexoes) : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use(async (contexto, proximo) =>
        {
            if (conexoes.TryGetValue(contexto.Connection.Id, out var conexao))
            {
                conexoes[contexto.Connection.Id] = conexao with { UltimoPedido = $"{contexto.Request.Method} {contexto.Request.Path}" };
            }

            await proximo(contexto);
        });
        next(app);
    };
}
