using System.Globalization;

namespace PAF.Latencia.Tests;

/// <summary>
/// One (call, painel) pair, in milliseconds on the test process clock. <see cref="DesdeEnvio"/> starts when the
/// guichê's request leaves; <see cref="DesdeConfirmacao"/> starts when its 200 arrives (RNF18's start, "o guichê
/// confirma a chamada") and is negative when the direct send after commit beat the HTTP response to the client.
/// </summary>
internal sealed record Amostra(double DesdeEnvio, double DesdeConfirmacao);

/// <summary>Percentiles of a phase by nearest rank, the way RNF18 states the target (p95 under 1 s).</summary>
internal sealed record Medicao(string Fase, int Chamadas, IReadOnlyList<Amostra> Amostras)
{
    public static readonly TimeSpan Meta = TimeSpan.FromSeconds(1);

    public double P95DesdeEnvio => Percentil(Amostras.Select(a => a.DesdeEnvio), 0.95);

    /// <summary>A painel that got the senha before the guichê got its 200 waited zero after the confirmation.</summary>
    public double P95DesdeConfirmacao => Percentil(Amostras.Select(a => Math.Max(0, a.DesdeConfirmacao)), 0.95);

    public static double Percentil(IEnumerable<double> valores, double p)
    {
        var ordenados = valores.Order().ToArray();
        if (ordenados.Length == 0)
        {
            throw new InvalidOperationException("Percentil de amostra vazia.");
        }

        return ordenados[(int)Math.Ceiling(p * ordenados.Length) - 1];
    }

    public string Relatorio()
    {
        var envio = Amostras.Select(a => a.DesdeEnvio).ToArray();
        var confirmacao = Amostras.Select(a => Math.Max(0, a.DesdeConfirmacao)).ToArray();
        var antes = Amostras.Count(a => a.DesdeConfirmacao < 0);
        return string.Create(CultureInfo.InvariantCulture,
            $"{Fase}: chamadas={Chamadas} amostras={Amostras.Count} | do envio: p50={Percentil(envio, 0.50):F1} p95={Percentil(envio, 0.95):F1} p99={Percentil(envio, 0.99):F1} max={envio.Max():F1} ms | do 200: p50={Percentil(confirmacao, 0.50):F1} p95={Percentil(confirmacao, 0.95):F1} p99={Percentil(confirmacao, 0.99):F1} max={confirmacao.Max():F1} ms, {antes} chegaram antes do 200");
    }
}
