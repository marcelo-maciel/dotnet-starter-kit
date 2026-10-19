using FSH.Modules.PAF.Aparencia;

namespace PAF.Tests;

public sealed class ContrasteWcagTests
{
    [Theory]
    [InlineData("#777777", "#FFFFFF", false)] // 4.48:1, just under the floor
    [InlineData("#767676", "#FFFFFF", true)]  // 4.54:1, just over it
    [InlineData("#FFFFFF", "#FFFF00", false)] // white on yellow, the totem case of RNF19
    [InlineData("#000000", "#FFFF00", true)]
    [InlineData("#FFFFFF", "#FFFFFF", false)]
    public void Suficiente_SoAPartirDe4Virgula5(string texto, string fundo, bool esperado) =>
        ContrasteWcag.Suficiente(texto, fundo).ShouldBe(esperado);

    [Fact]
    public void Razao_ExtremosESimetria()
    {
        ContrasteWcag.Razao("#000000", "#FFFFFF").ShouldBe(21, 0.001);
        ContrasteWcag.Razao("#ffffff", "#000000").ShouldBe(21, 0.001);
        ContrasteWcag.Razao("#123456", "#123456").ShouldBe(1, 0.001);
    }

    [Theory]
    [InlineData("#GGGGGG")]
    [InlineData("#FFF")]
    [InlineData("#FFFFFF80")]
    [InlineData("FFFFFF")]
    [InlineData(" #FFFFFF")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("#FFFFF\u0661")] // non-ASCII digit
    public void FormatoValido_SoRRGGBB(string? cor)
    {
        ContrasteWcag.FormatoValido(cor).ShouldBeFalse();
        if (cor is not null)
        {
            Should.Throw<ArgumentException>(() => ContrasteWcag.Razao(cor, "#000000"));
        }
    }

    [Theory]
    [InlineData("#a1B2c3")]
    [InlineData("#000000")]
    public void FormatoValido_AceitaMaiusculaEMinuscula(string cor) => ContrasteWcag.FormatoValido(cor).ShouldBeTrue();
}
