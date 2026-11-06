using FSH.Modules.PAF;

namespace PAF.Tests.PrazosEntradaSaida;

/// <summary>
/// RNF05 barrier, code half (O19): no I/O path of the PAF assembly may opt out of cancellation or of a deadline.
/// <c>CancellationToken.None</c> drops the request's token and <c>Timeout.InfiniteTimeSpan</c> removes the bound.
/// </summary>
public sealed class PrazosCodigoTests
{
    private const string CancellationTokenNone = "System.Threading.CancellationToken.get_None";
    private const string TimeoutInfinito = "System.Threading.Timeout.InfiniteTimeSpan";
    private const string CancelAfter = "System.Threading.CancellationTokenSource.CancelAfter";

    [Fact]
    public void AssemblyDoPaf_Should_NaoUsarCancellationTokenNoneNemTimeoutInfinito()
    {
        var referencias = VarreduraDeIl.Referencias(typeof(PafModule).Assembly.GetTypes());

        // Positive control: AvisosTempoReal bounds the hub send with CancelAfter, so a scan that misses it reads nothing.
        referencias.ShouldContain(r => r.Membro == CancelAfter,
            "a varredura não viu o CancelAfter de AvisosTempoReal; ela não está lendo o IL");

        var proibidas = referencias.Where(r => r.Membro is CancellationTokenNone or TimeoutInfinito).ToList();
        proibidas.ShouldBeEmpty(
            "Use o token da requisição ou um CancellationTokenSource com CancelAfter. Encontrado: " +
            string.Join("; ", proibidas.Select(r => $"{r.Membro} em {r.Metodo}")));
    }

    [Fact]
    public void Varredura_Should_Apontar_When_CodigoUsaCancellationTokenNoneETimeoutInfinito()
    {
        var referencias = VarreduraDeIl.Referencias(typeof(CodigoSemPrazo).GetNestedTypes(
            System.Reflection.BindingFlags.NonPublic).Append(typeof(CodigoSemPrazo)));

        referencias.ShouldContain(r => r.Membro == CancellationTokenNone && r.Metodo.Contains(nameof(CodigoSemPrazo), StringComparison.Ordinal));
        referencias.ShouldContain(r => r.Membro == TimeoutInfinito && r.Metodo.Contains(nameof(CodigoSemPrazo), StringComparison.Ordinal));
    }

    // Synthetic offender: an async method, so the forbidden references live inside the compiler's state machine.
    internal static class CodigoSemPrazo
    {
        internal static async Task<TimeSpan> EsperarAsync()
        {
            await Task.Delay(1, CancellationToken.None).ConfigureAwait(false);
            return Timeout.InfiniteTimeSpan;
        }
    }
}
