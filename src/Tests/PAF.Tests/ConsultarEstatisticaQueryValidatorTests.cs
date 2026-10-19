using FSH.Modules.PAF.Contracts.v1.Estatistica;
using FSH.Modules.PAF.Features.v1.Estatistica.ConsultarEstatistica;

namespace PAF.Tests;

public sealed class ConsultarEstatisticaQueryValidatorTests
{
    private static readonly ConsultarEstatisticaQueryValidator Validador = new();
    private static readonly DateOnly Dia = new(2026, 10, 18);

    private static ConsultarEstatisticaQuery Consulta(TipoEstatistica? tipo, DateOnly? inicial, DateOnly? final, AgrupamentoEstatistica? agrupamento) =>
        new() { Tipo = tipo, DataInicial = inicial, DataFinal = final, Agrupamento = agrupamento };

    private static string[] Erros(ConsultarEstatisticaQuery consulta) =>
        Validador.Validate(consulta).Errors.Select(e => e.PropertyName).ToArray();

    [Theory]
    [InlineData(TipoEstatistica.AtendimentosPorTempoDeEspera)]
    [InlineData(TipoEstatistica.AtendimentosPorAtendente)]
    [InlineData(TipoEstatistica.FichasEmitidasPorGuiche)]
    public void TipoSemAgrupamento_UmDiaSo_Valido(TipoEstatistica tipo)
    {
        Erros(Consulta(tipo, Dia, Dia, null)).ShouldBeEmpty();
    }

    [Theory]
    [InlineData(TipoEstatistica.TotaisPorTipoDeAtendimento, AgrupamentoEstatistica.Fila)]
    [InlineData(TipoEstatistica.TotalDeAtendimentos, AgrupamentoEstatistica.Setor)]
    public void Tipos9E10_ComAgrupamento_Validos(TipoEstatistica tipo, AgrupamentoEstatistica agrupamento)
    {
        Erros(Consulta(tipo, Dia, Dia.AddDays(30), agrupamento)).ShouldBeEmpty();
    }

    [Fact]
    public void DataFinalAnteriorAInicial_Invalida()
    {
        Erros(Consulta(TipoEstatistica.AtendimentosPorGuiche, Dia, Dia.AddDays(-1), null)).ShouldBe(["DataFinal"]);
    }

    [Fact]
    public void SemDatas_Invalida()
    {
        Erros(Consulta(TipoEstatistica.AtendimentosPorGuiche, null, null, null)).ShouldBe(["DataInicial", "DataFinal"], ignoreOrder: true);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(TipoEstatistica.Nenhum)]
    [InlineData((TipoEstatistica)11)]
    [InlineData((TipoEstatistica)(-1))]
    public void TipoAusenteOuForaDoCatalogo_Invalido(TipoEstatistica? tipo)
    {
        Erros(Consulta(tipo, Dia, Dia, null)).ShouldContain("Tipo");
    }

    [Fact]
    public void AgrupamentoNoTipo1_Invalido()
    {
        Erros(Consulta(TipoEstatistica.AtendimentosPorTempoDeEspera, Dia, Dia, AgrupamentoEstatistica.Fila)).ShouldBe(["Agrupamento"]);
    }

    [Theory]
    [InlineData(TipoEstatistica.TotaisPorTipoDeAtendimento)]
    [InlineData(TipoEstatistica.TotalDeAtendimentos)]
    public void Tipos9E10_SemAgrupamento_Invalidos(TipoEstatistica tipo)
    {
        Erros(Consulta(tipo, Dia, Dia, null)).ShouldBe(["Agrupamento"]);
    }

    [Fact]
    public void AgrupamentoForaDoCatalogo_Invalido()
    {
        Erros(Consulta(TipoEstatistica.TotalDeAtendimentos, Dia, Dia, (AgrupamentoEstatistica)7)).ShouldBe(["Agrupamento"]);
    }

    [Fact]
    public void SomenteEstacoesComAtendimento_SoNoTipo3()
    {
        Erros(new ConsultarEstatisticaQuery { Tipo = TipoEstatistica.AtendimentosPorGuiche, DataInicial = Dia, DataFinal = Dia, SomenteEstacoesComAtendimento = true })
            .ShouldBeEmpty();
        Erros(new ConsultarEstatisticaQuery { Tipo = TipoEstatistica.AtendimentosPorAtendente, DataInicial = Dia, DataFinal = Dia, SomenteEstacoesComAtendimento = true })
            .ShouldBe(["SomenteEstacoesComAtendimento"]);
    }

    [Fact]
    public void IdVazioNosFiltros_Invalido()
    {
        var erros = Erros(new ConsultarEstatisticaQuery
        {
            Tipo = TipoEstatistica.AtendimentosPorGuiche,
            DataInicial = Dia,
            DataFinal = Dia,
            Unidades = [Guid.Empty],
            Filas = [Guid.NewGuid(), Guid.Empty],
            Prioridades = [Guid.Empty],
        });

        erros.ShouldBe(["Unidades[0]", "Filas[1]", "Prioridades[0]"], ignoreOrder: true);
    }
}
