using FSH.Modules.PAF.Midias;

namespace PAF.Tests;

/// <summary>
/// M04: validity of the signed read URL (exhibition + margin, floor, the R2 cap of 7 days) and the cache lifetime that
/// keeps a cached URL covering the whole exhibition (ADR 0007).
/// </summary>
public sealed class MidiaUrlLeituraTests
{
    private static readonly OpcoesMidia Padrao = new();

    [Fact]
    public void MidiaDe3HorasDuasVezes_ValeSeisHorasMaisAMargem()
    {
        var exibicao = TimeSpan.FromHours(3) * 2;

        UrlLeituraMidia.Validade(exibicao, Padrao).ShouldBe(TimeSpan.FromHours(6) + TimeSpan.FromMinutes(10));
    }

    [Fact]
    public void MidiaCurta_ValeOPiso()
    {
        UrlLeituraMidia.Validade(TimeSpan.FromSeconds(10), Padrao).ShouldBe(TimeSpan.FromHours(1));
    }

    [Theory]
    [InlineData(7 * 86_400 - 900 - 600)]
    [InlineData(7 * 86_400 - 1)]
    [InlineData(7 * 86_400)]
    [InlineData(86_400 * 30)]
    public void AcimaDoTeto_ValeOTetoExato(int segundos)
    {
        UrlLeituraMidia.Validade(TimeSpan.FromSeconds(segundos), Padrao).ShouldBe(UrlLeituraMidia.TetoValidade);
    }

    /// <summary>R2 caps at 7 days; the 15-minute slack keeps the SDK's skew-corrected signature inside SigV4.</summary>
    [Fact]
    public void Teto_SeteDiasMenosAFolgaDeRelogioDoS3()
    {
        UrlLeituraMidia.TetoValidade.ShouldBe(TimeSpan.FromDays(7) - TimeSpan.FromMinutes(15));
    }

    [Fact]
    public void LogoAbaixoDoTeto_NaoArredonda()
    {
        var exibicao = UrlLeituraMidia.TetoValidade - TimeSpan.FromMinutes(10) - TimeSpan.FromSeconds(1);

        UrlLeituraMidia.Validade(exibicao, Padrao).ShouldBe(UrlLeituraMidia.TetoValidade - TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void PisoEMargemConfigurados_SaoOsUsados()
    {
        var opcoes = new OpcoesMidia { PisoValidadeUrlLeitura = TimeSpan.FromMinutes(2), MargemLeitura = TimeSpan.FromSeconds(30) };

        UrlLeituraMidia.Validade(TimeSpan.FromSeconds(10), opcoes).ShouldBe(TimeSpan.FromMinutes(2));
        UrlLeituraMidia.Validade(TimeSpan.FromMinutes(5), opcoes).ShouldBe(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(30));
    }

    /// <summary>
    /// The invariant the cache exists under: a URL handed out at the last instant of its cache entry still has the
    /// whole exhibition ahead of it, and the entry always ends before the URL does.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(10)]
    [InlineData(3_599)]
    [InlineData(3_600)]
    [InlineData(21_600)]
    [InlineData(86_400)]
    [InlineData(86_400 * 3)]
    [InlineData(7 * 86_400 - 900 - 601)]
    public void EntradaDoCache_TerminaComAExibicaoInteiraAindaCoberta(int segundos)
    {
        var exibicao = TimeSpan.FromSeconds(segundos);
        var validade = UrlLeituraMidia.Validade(exibicao, Padrao);

        var cache = UrlLeituraMidia.DuracaoCache(validade, exibicao, Padrao);

        cache.ShouldBeGreaterThan(TimeSpan.Zero);
        cache.ShouldBeLessThan(validade);
        (validade - cache).ShouldBeGreaterThanOrEqualTo(exibicao);
    }

    [Fact]
    public void ExibicaoMaiorQueOTeto_CacheDuraAMargem()
    {
        var exibicao = TimeSpan.FromDays(8);
        var validade = UrlLeituraMidia.Validade(exibicao, Padrao);

        UrlLeituraMidia.DuracaoCache(validade, exibicao, Padrao).ShouldBe(Padrao.MargemLeitura);
    }
}
