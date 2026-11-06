using FSH.Modules.PAF.Estacoes;

namespace PAF.Tests;

/// <summary>Horário de operação do aviso do RNF20 (D24): união das janelas das filas ativas da unidade.</summary>
public sealed class HorarioDeOperacaoTests
{
    private static readonly JanelaDaFila Manha = new(true, new TimeOnly(8, 0), new TimeOnly(12, 0));
    private static readonly JanelaDaFila Tarde = new(true, new TimeOnly(14, 0), new TimeOnly(18, 0));

    [Theory]
    [InlineData("07:59:59", false)]
    [InlineData("08:00:00", true)]
    [InlineData("11:59:00", true)]
    [InlineData("12:00:00", true)]
    [InlineData("12:00:01", false)]
    [InlineData("13:00:00", false)]
    [InlineData("14:00:00", true)]
    [InlineData("17:59:00", true)]
    [InlineData("18:00:00", true)]
    [InlineData("18:01:00", false)]
    [InlineData("23:59:59", false)]
    [InlineData("00:00:00", false)]
    public void DuasFilas_UniaoDasJanelas_PontasInclusivas(string hora, bool esperado) =>
        HorarioDeOperacao.EmOperacao([Manha, Tarde], TimeOnly.Parse(hora, System.Globalization.CultureInfo.InvariantCulture)).ShouldBe(esperado);

    [Fact]
    public void UnidadeSemFila_NuncaEmOperacao()
    {
        HorarioDeOperacao.EmOperacao([], new TimeOnly(11, 59)).ShouldBeFalse();
        HorarioDeOperacao.EmOperacao([], new TimeOnly(0, 0)).ShouldBeFalse();
    }

    [Fact]
    public void FilaDesativada_NaoEntraNaUniao()
    {
        var tardeDesativada = Tarde with { Ativa = false };

        HorarioDeOperacao.EmOperacao([Manha, tardeDesativada], new TimeOnly(17, 59)).ShouldBeFalse();
        HorarioDeOperacao.EmOperacao([Manha, tardeDesativada], new TimeOnly(11, 59)).ShouldBeTrue();
        HorarioDeOperacao.EmOperacao([Manha with { Ativa = false }, tardeDesativada], new TimeOnly(11, 59))
            .ShouldBeFalse("só filas desativadas é o mesmo que unidade sem fila ativa");
    }

    [Fact]
    public void FilaAtivaSemJanela_OperaODiaInteiro_ComoNaEmissao()
    {
        var semJanela = new JanelaDaFila(true, null, null);

        HorarioDeOperacao.EmOperacao([semJanela], new TimeOnly(3, 0)).ShouldBeTrue();
        HorarioDeOperacao.EmOperacao([Manha, semJanela], new TimeOnly(13, 0)).ShouldBeTrue();
        HorarioDeOperacao.EmOperacao([semJanela with { Ativa = false }], new TimeOnly(3, 0)).ShouldBeFalse();
    }
}
