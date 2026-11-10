using System.Globalization;
using System.Text.RegularExpressions;

namespace FSH.Modules.PAF.Aparencia;

/// <summary>
/// Color rules of the appearance cadastros (RF02, RF04, RNF19): only opaque <c>#RRGGBB</c>, because an alpha or a
/// short form would make the contrast depend on what is behind it, and the WCAG 2.2 contrast ratio between two colors.
/// </summary>
public static partial class Contraste
{
    /// <summary>WCAG 2.2 success criterion 1.4.3 (AA, normal text), required by RNF19.</summary>
    public const double RazaoMinima = 4.5;

    [GeneratedRegex("^#[0-9A-Fa-f]{6}$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex CorHexadecimal();

    public static bool CorValida(string? cor) => cor is not null && CorHexadecimal().IsMatch(cor);

    /// <summary>Ratio (L1 + 0.05) / (L2 + 0.05), lighter over darker; both colors must pass <see cref="CorValida"/>.</summary>
    public static double Razao(string cor1, string cor2)
    {
        var l1 = Luminancia(cor1);
        var l2 = Luminancia(cor2);
        return (Math.Max(l1, l2) + 0.05) / (Math.Min(l1, l2) + 0.05);
    }

    // Compared unrounded: #767676 on white is 4.54 and passes, #777777 is 4.48 and fails; rounding would blur the edge.
    public static bool Suficiente(string texto, string fundo) => Razao(texto, fundo) >= RazaoMinima;

    internal static double Luminancia(string cor)
    {
        if (!CorValida(cor))
        {
            throw new ArgumentException("Cor fora do formato #RRGGBB.", nameof(cor));
        }

        var r = Canal(cor, 1);
        var g = Canal(cor, 3);
        var b = Canal(cor, 5);
        return (0.2126 * r) + (0.7152 * g) + (0.0722 * b);
    }

    private static double Canal(string cor, int inicio)
    {
        var c = int.Parse(cor.AsSpan(inicio, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255.0;
        return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
    }
}
