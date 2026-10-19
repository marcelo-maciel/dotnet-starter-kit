using FSH.Modules.PAF.Parametros;

namespace PAF.Tests;

/// <summary>C10 catalog: every RF06 key validates its own default, the hotkey rule and the inheritance of one snapshot.</summary>
public sealed class ParametrosRf06Tests
{
    private static readonly Dictionary<string, string> Nada = new(StringComparer.Ordinal);

    [Fact]
    public void Catalogo_ChavesUnicas_EPadraoAceitoPelaPropriaValidacao()
    {
        CatalogoParametros.Todos.Select(p => p.Chave).ShouldBeUnique();
        foreach (var parametro in CatalogoParametros.Todos)
        {
            parametro.Normalizar(parametro.Padrao).ShouldBe(parametro.Padrao, parametro.Chave);
        }
    }

    [Fact]
    public void Catalogo_DeixaDeForaOLogotipoEOsGeraisDeC20()
    {
        var chaves = CatalogoParametros.Todos.Select(p => p.Chave).ToList();
        chaves.ShouldContain(CatalogoParametros.SomenteFichasDaDataAtual.Chave);
        chaves.ShouldNotContain(c => c.Contains("logotipo", StringComparison.OrdinalIgnoreCase) || c.Contains("voz", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("F1", "F1")]
    [InlineData("f12", "F12")]
    [InlineData(" F7 ", "F7")]
    public void Tecla_AceitaDeF1AF12(string texto, string esperado)
    {
        var (valido, valor) = CatalogoParametros.InterpretarTecla(texto);
        valido.ShouldBeTrue();
        valor.ShouldBe(esperado);
    }

    [Theory]
    [InlineData("F0")]
    [InlineData("F13")]
    [InlineData("F01")]
    [InlineData("A")]
    [InlineData("F")]
    [InlineData("Enter")]
    [InlineData("")]
    [InlineData("F+1")]
    public void Tecla_RecusaOResto(string texto) => CatalogoParametros.InterpretarTecla(texto).Valido.ShouldBeFalse();

    [Theory]
    [InlineData("1", true)]
    [InlineData("30", true)]
    [InlineData("0", false)]
    [InlineData("31", false)]
    [InlineData(" 5", false)]
    [InlineData("1.5", false)]
    public void QuantidadeDoPainel_DeUmATrinta(string texto, bool valido) =>
        CatalogoParametros.QuantidadeChamadasPainel.Interpretar(texto).Valido.ShouldBe(valido);

    [Fact]
    public void Texto_RecusaAcimaDoMaximoEControle_ERecortaEspacos()
    {
        CatalogoParametros.TituloFichas.Interpretar(new string('á', CatalogoParametros.TamanhoMaximoTitulo)).Valido.ShouldBeTrue();
        CatalogoParametros.TituloFichas.Interpretar(new string('á', CatalogoParametros.TamanhoMaximoTitulo + 1)).Valido.ShouldBeFalse();
        CatalogoParametros.MensagemRodapeFicha.Interpretar("a\tb").Valido.ShouldBeFalse();
        CatalogoParametros.TituloFichas.Interpretar("  Loja  ").ShouldBe((true, "Loja"));
        CatalogoParametros.TituloFichas.Interpretar(string.Empty).ShouldBe((true, string.Empty));
    }

    [Fact]
    public void Booleano_GravadoNaFormaCanonica()
    {
        var qr = CatalogoParametros.Buscar(CatalogoParametros.ImprimirQrFicha.Chave)!;
        qr.Normalizar("TRUE").ShouldBe("true");
        qr.Normalizar("sim").ShouldBeNull();
        CatalogoParametros.Buscar("naoExiste").ShouldBeNull();
    }

    [Fact]
    public void Teclas_PadroesDoLegadoSemConflito()
    {
        CatalogoParametros.Teclas.Select(t => t.Padrao).ShouldBe(["F7", "F8", "F4", "F6", "F3", "F2"]);
        ConflitoDeTeclas.Encontrar(Nada, Nada).ShouldBeNull();
    }

    [Fact]
    public void Teclas_ConflitoEntreNiveis_EInvalidaContaComoPadrao()
    {
        var empresa = new Dictionary<string, string>(StringComparer.Ordinal) { [CatalogoParametros.TeclaEncerrar.Chave] = "F9" };
        var unidade = new Dictionary<string, string>(StringComparer.Ordinal) { [CatalogoParametros.TeclaChamarProxima.Chave] = "f9" };

        ConflitoDeTeclas.Encontrar(empresa, Nada).ShouldBeNull();
        var repetida = ConflitoDeTeclas.Encontrar(empresa, unidade).ShouldNotBeNull();
        repetida.Tecla.ShouldBe("F9");
        repetida.PrimeiraChave.ShouldBe(CatalogoParametros.TeclaChamarProxima.Chave);
        repetida.SegundaChave.ShouldBe(CatalogoParametros.TeclaEncerrar.Chave);

        // An unreadable stored key reads as its default (F4 for encerrar), so it collides with an override to F4.
        var invalida = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [CatalogoParametros.TeclaEncerrar.Chave] = "xyz",
            [CatalogoParametros.TeclaIniciar.Chave] = "F4",
        };
        ConflitoDeTeclas.Encontrar(Nada, invalida).ShouldNotBeNull().Tecla.ShouldBe("F4");
    }

    [Fact]
    public void Snapshot_UnidadeSobreEmpresaSobreFabrica_InvalidoCaiNoPadraoComAviso()
    {
        var avisos = new List<(string, OrigemParametro)>();
        var quantidade = CatalogoParametros.QuantidadeChamadasPainel;
        var titulo = CatalogoParametros.TituloFichas;
        var parametros = new ParametrosDaUnidade(
            new Dictionary<string, string>(StringComparer.Ordinal) { [quantidade.Chave] = "6", [titulo.Chave] = "Rede" },
            new Dictionary<string, string>(StringComparer.Ordinal) { [quantidade.Chave] = "4", [titulo.Chave] = new string('x', 500) },
            (chave, origem) => avisos.Add((chave, origem)));

        parametros.Ler(quantidade).ShouldBe(4);
        parametros.Efetivo(CatalogoParametros.Buscar(quantidade.Chave)!).ShouldBe(("4", OrigemParametro.Unidade));
        parametros.Ler(CatalogoParametros.ImprimirQrFicha).ShouldBeFalse();
        parametros.Efetivo(CatalogoParametros.Buscar(CatalogoParametros.OrdenacaoFilas.Chave)!).ShouldBe(("ordem", OrigemParametro.Fabrica));

        // The unidade row is unreadable: the default wins, not the empresa row, exactly as LerAsync resolves it.
        parametros.Ler(titulo).ShouldBe(string.Empty);
        avisos.ShouldBe([(titulo.Chave, OrigemParametro.Unidade)]);
    }
}
