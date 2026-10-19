using FSH.Modules.PAF.Aparencia;

namespace PAF.Tests;

/// <summary>C17, D16: text color against the worst point of a background image, from the 256-band luminance sample.</summary>
public sealed class ContrasteImagemTests
{
    private static readonly string SoBranco = new string('0', 63) + "1";
    private static readonly string SoPreto = "8" + new string('0', 63);

    [Theory]
    [InlineData("#000000", true)]
    [InlineData("#767676", true)]  // 4.52:1 against the lower edge of the top band, still over the floor
    [InlineData("#777777", false)] // 4.46:1
    [InlineData("#FFFFFF", false)]
    public void ImagemBranca_SoTextoEscuroPassa(string texto, bool esperado) =>
        ContrasteImagem.Suficiente(texto, SoBranco).ShouldBe(esperado);

    [Fact]
    public void ImagemPreta_TextoBrancoPassa_PretoNao()
    {
        ContrasteImagem.PiorRazao("#FFFFFF", SoPreto).ShouldBe(1.05 / (1.0 / 256 + 0.05), 1e-9);
        ContrasteImagem.Suficiente("#FFFFFF", SoPreto).ShouldBeTrue();
        ContrasteImagem.Suficiente("#000000", SoPreto).ShouldBeFalse();
    }

    [Fact]
    public void ImagemComPretoEBranco_NenhumaCorPassa()
    {
        var ambos = "8" + new string('0', 62) + "1";
        foreach (var cor in new[] { "#000000", "#FFFFFF", "#767676", "#FF0000", "#00FF00" })
        {
            ContrasteImagem.Suficiente(cor, ambos).ShouldBeFalse(cor);
        }
    }

    [Fact]
    public void PiorPonto_EaFaixaMaisProximaDaCorDoTexto()
    {
        // Bands 4 (dark) and 200 (light): white text is limited by band 200, black text by band 4, each at its nearest edge.
        var amostra = Amostra(4, 200);
        ContrasteImagem.PiorRazao("#FFFFFF", amostra).ShouldBe(1.05 / (201.0 / 256 + 0.05), 1e-9);
        ContrasteImagem.PiorRazao("#000000", amostra).ShouldBe((4.0 / 256 + 0.05) / 0.05, 1e-9);
    }

    [Fact]
    public void CorDentroDeUmaFaixaOcupada_Razao1()
    {
        // #808080 has relative luminance 0.2159, inside band 55.
        ContrasteImagem.PiorRazao("#808080", Amostra(55)).ShouldBe(1, 1e-9);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("000000000000000000000000000000000000000000000000000000000000000")]   // 63 digits
    [InlineData("00000000000000000000000000000000000000000000000000000000000000001")] // 65 digits
    [InlineData("0000000000000000000000000000000000000000000000000000000000000000")]  // no band: would pass any color
    [InlineData("G000000000000000000000000000000000000000000000000000000000000000")]
    public void AmostraForaDoFormato_Invalida(string? amostra)
    {
        ContrasteImagem.AmostraValida(amostra).ShouldBeFalse();
        if (amostra is not null)
        {
            Should.Throw<ArgumentException>(() => ContrasteImagem.PiorRazao("#000000", amostra));
        }
    }

    [Fact]
    public void AmostraAceitaMaiusculaEMinuscula() =>
        ContrasteImagem.AmostraValida("Ff" + new string('0', 62)).ShouldBeTrue();

    private static string Amostra(params int[] faixas)
    {
        var digitos = new int[ContrasteImagem.DigitosDaAmostra];
        foreach (var f in faixas)
        {
            digitos[f / 4] |= 8 >> (f % 4);
        }

        return string.Concat(digitos.Select(d => d.ToString("X", System.Globalization.CultureInfo.InvariantCulture)));
    }
}
