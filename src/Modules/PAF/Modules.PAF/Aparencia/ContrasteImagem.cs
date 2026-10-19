using System.Globalization;

namespace FSH.Modules.PAF.Aparencia;

/// <summary>
/// Contrast of a text color against a background image (C17, REQUISITOS 8.1 l.990, D16): the worst point of the image
/// decides. The image arrives as a luminance occupancy sample (see <c>ImagemFundoDto.Luminancias</c>): 256 bands of WCAG
/// relative luminance, each band read as the whole closed interval [i/256, (i+1)/256], so the check is never laxer than
/// the pixels behind it (it may refuse a color up to one band width early).
/// </summary>
public static class ContrasteImagem
{
    public const int Faixas = 256;

    /// <summary>Hex digits of a sample: 4 bands per digit.</summary>
    public const int DigitosDaAmostra = Faixas / 4;

    /// <summary>64 hex digits with at least one band set: an empty sample would pass every color.</summary>
    public static bool AmostraValida(string? luminancias)
    {
        if (luminancias is null || luminancias.Length != DigitosDaAmostra)
        {
            return false;
        }

        var algumaFaixa = false;
        foreach (var c in luminancias)
        {
            if (!char.IsAsciiHexDigit(c))
            {
                return false;
            }

            algumaFaixa |= c != '0';
        }

        return algumaFaixa;
    }

    /// <summary>
    /// Lowest ratio between <paramref name="cor"/> and any point of the sampled image; 1 when the color's luminance falls
    /// inside an occupied band. Both inputs must already be valid (<see cref="Contraste.CorValida"/>, <see cref="AmostraValida"/>).
    /// </summary>
    public static double PiorRazao(string cor, string luminancias)
    {
        if (!AmostraValida(luminancias))
        {
            throw new ArgumentException("Amostra de luminância fora do formato.", nameof(luminancias));
        }

        var texto = Contraste.Luminancia(cor);
        var pior = double.MaxValue;
        for (var faixa = 0; faixa < Faixas; faixa++)
        {
            if (!Ocupada(luminancias, faixa))
            {
                continue;
            }

            // The point of the band closest to the text luminance is the worst one of the band.
            var maisProximo = Math.Clamp(texto, (double)faixa / Faixas, (double)(faixa + 1) / Faixas);
            var razao = (Math.Max(texto, maisProximo) + 0.05) / (Math.Min(texto, maisProximo) + 0.05);
            pior = Math.Min(pior, razao);
        }

        return pior;
    }

    public static bool Suficiente(string cor, string luminancias) => PiorRazao(cor, luminancias) >= Contraste.RazaoMinima;

    private static bool Ocupada(string luminancias, int faixa)
    {
        var digito = int.Parse(luminancias.AsSpan(faixa / 4, 1), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return (digito & (8 >> (faixa % 4))) != 0;
    }
}
