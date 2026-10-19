using System.Buffers;
using System.Globalization;

namespace FSH.Modules.PAF.Aparencia;

/// <summary>
/// Contrast floor of every appearance the client picks (RNF19, REQ 8.1): text over background at least 4.5:1 by the
/// WCAG 2.x relative-luminance formula. Single definition for the fila colors (C09) and the station blocks (C04), and
/// mirrored by <c>clients/dashboard/src/features/paf/aparencia/contraste.ts</c> for the preview.
/// </summary>
public static class ContrasteWcag
{
    public const double RazaoMinima = 4.5;

    public const string MensagemFormato = "Cor no formato #RRGGBB.";

    private static readonly SearchValues<char> Hexadecimais = SearchValues.Create("0123456789abcdefABCDEF");

    // Only #RRGGBB: a short form or an alpha channel would let the stored color differ from the one checked.
    public static bool FormatoValido(string? cor) =>
        cor is { Length: 7 } && cor[0] == '#' && !cor.AsSpan(1).ContainsAnyExcept(Hexadecimais);

    /// <summary>Ratio between the two colors, from 1 (same luminance) to 21 (black on white). Both must be <c>#RRGGBB</c>.</summary>
    public static double Razao(string corTexto, string corFundo)
    {
        double l1 = Luminancia(corTexto);
        double l2 = Luminancia(corFundo);
        return (Math.Max(l1, l2) + 0.05) / (Math.Min(l1, l2) + 0.05);
    }

    public static bool Suficiente(string corTexto, string corFundo) => Razao(corTexto, corFundo) >= RazaoMinima;

    private static double Luminancia(string cor)
    {
        if (!FormatoValido(cor))
        {
            throw new ArgumentException(MensagemFormato, nameof(cor));
        }

        return (0.2126 * Canal(cor, 1)) + (0.7152 * Canal(cor, 3)) + (0.0722 * Canal(cor, 5));
    }

    private static double Canal(string cor, int inicio)
    {
        double c = int.Parse(cor.AsSpan(inicio, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255.0;
        return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
    }
}
