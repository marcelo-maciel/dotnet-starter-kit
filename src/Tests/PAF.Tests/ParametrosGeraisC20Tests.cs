using FSH.Modules.PAF.Parametros;

namespace PAF.Tests;

/// <summary>
/// C20, the RF06 Geral keys: the voice is never a switch (D29, RNF19), the call message is bounded printable text and
/// the detailed fila information is a plain boolean (D30), all three editable in the Geral group.
/// </summary>
public sealed class ParametrosGeraisC20Tests
{
    private static ParametroCatalogado Catalogado(string chave) => CatalogoParametros.Buscar(chave).ShouldNotBeNull();

    [Theory]
    [InlineData("false")]
    [InlineData("true")]
    [InlineData("desligada")]
    [InlineData("")]
    [InlineData("SENHA")]
    public void Voz_NaoDesliga_SoAceitaAsDuasFalas(string valor)
    {
        Catalogado(CatalogoParametros.ChamarFichaPorVoz.Chave).Normalizar(valor).ShouldBeNull();
    }

    [Theory]
    [InlineData("senha", "senha")]
    [InlineData(" senhaEMensagem ", "senhaEMensagem")]
    public void Voz_AsDuasFalasValem(string valor, string esperado)
    {
        Catalogado(CatalogoParametros.ChamarFichaPorVoz.Chave).Normalizar(valor).ShouldBe(esperado);
        CatalogoParametros.ChamarFichaPorVoz.Padrao.ShouldBe(CatalogoParametros.FalaSoSenha, "requisitos l.86: a voz fala só a senha");
    }

    [Fact]
    public void Mensagem_AteOMaximo_AparadaEVaziaValem_AcimaOuComControleNao()
    {
        var mensagem = Catalogado(CatalogoParametros.MensagemAoChamar.Chave);
        var maxima = new string('á', CatalogoParametros.TamanhoMaximoMensagemAoChamar);

        mensagem.Normalizar(maxima).ShouldBe(maxima);
        mensagem.Normalizar("  Dirija-se ao guichê  ").ShouldBe("Dirija-se ao guichê");
        mensagem.Normalizar(string.Empty).ShouldBe(string.Empty);
        mensagem.Normalizar(maxima + "x").ShouldBeNull();
        mensagem.Normalizar("linha 1\nlinha 2").ShouldBeNull();
    }

    [Fact]
    public void Detalhada_BooleanaDesligadaPorPadrao_NoGrupoGeral()
    {
        var detalhada = Catalogado(CatalogoParametros.ExibirInformacaoDetalhadaFila.Chave);
        detalhada.Tipo.ShouldBe(TipoParametro.Booleano);
        detalhada.Padrao.ShouldBe("false");
        detalhada.Normalizar("sim").ShouldBeNull();

        new[] { CatalogoParametros.ExibirInformacaoDetalhadaFila.Chave, CatalogoParametros.ChamarFichaPorVoz.Chave, CatalogoParametros.MensagemAoChamar.Chave }
            .Select(c => Catalogado(c).Grupo)
            .ShouldAllBe(g => g == "Geral");
    }
}
