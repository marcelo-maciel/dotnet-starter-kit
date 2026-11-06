using FSH.Modules.PAF.Parametros;

namespace PAF.Tests;

/// <summary>C19 catalog entry: empty is the system logo, an id is stored in one canonical form, anything else is refused.</summary>
public sealed class LogotipoC19Tests
{
    [Fact]
    public void Logotipo_NoGrupoLogotipo_TipoImagem_PadraoVazio()
    {
        var logotipo = CatalogoParametros.Buscar(CatalogoParametros.Logotipo.Chave).ShouldNotBeNull();
        logotipo.Grupo.ShouldBe("Logotipo");
        logotipo.Tipo.ShouldBe(TipoParametro.Imagem);
        logotipo.Padrao.ShouldBe(string.Empty);
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("   ", "")]
    [InlineData("0199F2A4-7B1C-7D3E-8A5B-1234567890AB", "0199f2a4-7b1c-7d3e-8a5b-1234567890ab")]
    [InlineData(" 0199f2a4-7b1c-7d3e-8a5b-1234567890ab ", "0199f2a4-7b1c-7d3e-8a5b-1234567890ab")]
    public void Logotipo_VazioOuId_NaFormaCanonica(string texto, string esperado) =>
        CatalogoParametros.Logotipo.Interpretar(texto).ShouldBe((true, esperado));

    [Theory]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    [InlineData("0199f2a47b1c7d3e8a5b1234567890ab")]
    [InlineData("{0199f2a4-7b1c-7d3e-8a5b-1234567890ab}")]
    [InlineData("logo.png")]
    [InlineData("https://exemplo.com/logo.png")]
    [InlineData("paf-midia/t1/u1/abc.png")]
    public void Logotipo_RecusaOQueNaoEIdDeMidia(string texto)
    {
        CatalogoParametros.Logotipo.Interpretar(texto).Valido.ShouldBeFalse();
        CatalogoParametros.Buscar(CatalogoParametros.Logotipo.Chave)!.Normalizar(texto).ShouldBeNull();
    }
}
