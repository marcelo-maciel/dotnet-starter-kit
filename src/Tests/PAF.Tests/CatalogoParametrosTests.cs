using FSH.Modules.PAF.Parametros;

namespace PAF.Tests;

public sealed class CatalogoParametrosTests
{
    [Fact]
    public void SomenteFichasDaDataAtual_VemLigadoDeFabrica()
    {
        CatalogoParametros.SomenteFichasDaDataAtual.Padrao.ShouldBeTrue("requisitos seção 4: ligado por padrão");
        CatalogoParametros.SomenteFichasDaDataAtual.Chave.ShouldBe("somenteFichasDaDataAtual");
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    [InlineData("False", false)]
    [InlineData(" true ", true)]
    public void InterpretarBooleano_AceitaTrueEFalse(string texto, bool esperado)
    {
        var (valido, valor) = CatalogoParametros.InterpretarBooleano(texto);
        valido.ShouldBeTrue();
        valor.ShouldBe(esperado);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("")]
    [InlineData("1")]
    [InlineData("sim")]
    public void InterpretarBooleano_RecusaOQueNaoEBooleano(string texto) =>
        CatalogoParametros.InterpretarBooleano(texto).Valido.ShouldBeFalse();
}
