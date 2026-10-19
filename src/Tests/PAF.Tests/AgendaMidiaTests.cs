using FSH.Modules.PAF.Domain;
using FSH.Modules.PAF.Midias;

namespace PAF.Tests;

/// <summary>RF10 time rules (M05, D13): window, midnight, days of the week, overlap and the São Paulo wall clock.</summary>
public sealed class AgendaMidiaTests
{
    // 2026-10-19 is a Monday, 2026-10-18 a Sunday.
    private static readonly DateOnly Segunda = new(2026, 10, 19);
    private static readonly DateOnly Domingo = new(2026, 10, 18);

    private static ProgramacaoDoDia Programacao(
        TimeOnly inicio, int tempoMidia, int repeticoes, DiasSemana dias, DateOnly de, DateOnly ate, string nome) =>
        new(Guid.CreateVersion7(), Guid.CreateVersion7(), nome, de, ate, inicio, repeticoes, dias, tempoMidia,
            new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero));

    private static ProgramacaoDoDia Programacao(TimeOnly inicio, int tempoMidia, int repeticoes, DiasSemana dias) =>
        Programacao(inicio, tempoMidia, repeticoes, dias, new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31), "p");

    private static DateTime Em(DateOnly dia, int hora, int minuto, int segundo) =>
        dia.ToDateTime(new TimeOnly(hora, minuto, segundo));

    [Fact]
    public void TemposCalculados_DuracaoVezesRepeticoes_HoraFinalEmSegundosDoDia()
    {
        var p = Programacao(new TimeOnly(10, 0), tempoMidia: 900, repeticoes: 2, DiasSemana.Todos);

        p.TempoTotalSegundos.ShouldBe(1800);
        p.HoraFinalSegundos.ShouldBe(10 * 3600 + 1800, "10:00 + 2 x 15 min = 10:30");
    }

    [Fact]
    public void TempoTotal_SemEstourarInteiro_ComDuracaoMaximaVezesMuitasRepeticoes()
    {
        AgendaMidia.TempoTotalSegundos(Midia.DuracaoMaximaSegundos, 86_400).ShouldBe(86_400L * 86_400);
    }

    [Theory]
    [InlineData(23, 0, 0, 3600, 1, true)]   // ends exactly at midnight
    [InlineData(23, 0, 0, 3601, 1, false)]  // one second past midnight
    [InlineData(0, 0, 0, 86_400, 1, true)]  // the whole day
    [InlineData(23, 59, 59, 1, 1, true)]
    [InlineData(23, 59, 59, 1, 2, false)]
    [InlineData(12, 0, 0, 600, 72, true)]   // 12 h exactly
    [InlineData(12, 0, 0, 600, 73, false)]
    public void D13_TerminaAteAMeiaNoite_LimiteInclusivoEm24h(int h, int m, int s, int tempoMidia, int repeticoes, bool aceita)
    {
        AgendaMidia.TerminaAteAMeiaNoite(new TimeOnly(h, m, s), tempoMidia, repeticoes).ShouldBe(aceita);
    }

    [Fact]
    public void NoAr_JanelaFechadaNoInicio_AbertaNoFim()
    {
        var p = Programacao(new TimeOnly(10, 0), tempoMidia: 900, repeticoes: 2, DiasSemana.Todos);

        AgendaMidia.NoAr([p], Em(Segunda, 9, 59, 59)).ShouldBeNull();
        AgendaMidia.NoAr([p], Em(Segunda, 10, 0, 0)).ShouldBe(p);
        AgendaMidia.NoAr([p], Em(Segunda, 10, 29, 59)).ShouldBe(p);
        AgendaMidia.NoAr([p], Em(Segunda, 10, 30, 0)).ShouldBeNull("10:30 is the end, outside the window");
    }

    [Fact]
    public void NoAr_TodosOsDiasSempre_SoSegundaNaoApareceNoDomingo()
    {
        var todos = Programacao(new TimeOnly(8, 0), tempoMidia: 3600, repeticoes: 2, DiasSemana.Todos);
        var soSegunda = Programacao(new TimeOnly(9, 0), tempoMidia: 3600, repeticoes: 1, DiasSemana.Segunda);

        for (var dia = Domingo; dia < Domingo.AddDays(7); dia = dia.AddDays(1))
        {
            AgendaMidia.NoAr([todos], Em(dia, 8, 30, 0)).ShouldBe(todos, $"todos os dias, {dia.DayOfWeek}");
            AgendaMidia.ValeNoDia(soSegunda, dia).ShouldBe(dia.DayOfWeek == DayOfWeek.Monday, dia.DayOfWeek.ToString());
        }

        AgendaMidia.NoAr([soSegunda], Em(Domingo, 9, 30, 0)).ShouldBeNull();
        AgendaMidia.NoAr([soSegunda], Em(Segunda, 9, 30, 0)).ShouldBe(soSegunda);
    }

    [Fact]
    public void NoAr_ForaDoPeriodo_NaoAparece_LimitesInclusivos()
    {
        var p = Programacao(new TimeOnly(8, 0), 3600, 1, DiasSemana.Todos, Segunda, Segunda.AddDays(2), "periodo");

        AgendaMidia.NoAr([p], Em(Segunda.AddDays(-1), 8, 30, 0)).ShouldBeNull();
        AgendaMidia.NoAr([p], Em(Segunda, 8, 30, 0)).ShouldBe(p);
        AgendaMidia.NoAr([p], Em(Segunda.AddDays(2), 8, 30, 0)).ShouldBe(p);
        AgendaMidia.NoAr([p], Em(Segunda.AddDays(3), 8, 30, 0)).ShouldBeNull();
    }

    [Fact]
    public void D13_Sobreposicao_VenceAHoraInicialMaisRecente_AteTerminar()
    {
        var longa = Programacao(new TimeOnly(9, 0), tempoMidia: 3600, repeticoes: 3, DiasSemana.Todos);   // 09:00-12:00
        var curta = Programacao(new TimeOnly(10, 0), tempoMidia: 1800, repeticoes: 1, DiasSemana.Todos);  // 10:00-10:30

        AgendaMidia.NoAr([curta, longa], Em(Segunda, 9, 59, 59)).ShouldBe(longa);
        AgendaMidia.NoAr([longa, curta], Em(Segunda, 10, 0, 0)).ShouldBe(curta);
        AgendaMidia.NoAr([longa, curta], Em(Segunda, 10, 29, 59)).ShouldBe(curta);
        AgendaMidia.NoAr([curta, longa], Em(Segunda, 10, 30, 0)).ShouldBe(longa, "the later one ended, the earlier one is back");
    }

    [Fact]
    public void D13_MesmaHoraInicial_DesempateDeterministicoPelaMaisNova()
    {
        var velha = Programacao(new TimeOnly(9, 0), 3600, 1, DiasSemana.Todos) with { CriadoEm = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero) };
        var nova = velha with { Id = Guid.CreateVersion7(), CriadoEm = velha.CriadoEm.AddMinutes(1) };

        AgendaMidia.NoAr([velha, nova], Em(Segunda, 9, 10, 0)).ShouldBe(nova);
        AgendaMidia.NoAr([nova, velha], Em(Segunda, 9, 10, 0)).ShouldBe(nova);
    }

    [Fact]
    public void FimEmpurradoAlemDaMeiaNoitePelaDuracao_TocaAteAMeiaNoite_NaoVazaParaODiaSeguinte()
    {
        // Possible only when the mídia's duration grows after the cadastro (the cadastro refuses it).
        var p = Programacao(new TimeOnly(23, 0), tempoMidia: 7200, repeticoes: 1, DiasSemana.Segunda);

        AgendaMidia.NoAr([p], Em(Segunda, 23, 59, 59)).ShouldBe(p);
        AgendaMidia.NoAr([p], Em(Segunda.AddDays(1), 0, 30, 0)).ShouldBeNull();
    }

    [Fact]
    public void ProgramacaoDas2130Brt_NoArAs0030Utc_DoDiaSeguinteEmUtc()
    {
        // 2026-10-20 00:30 UTC (a Tuesday in UTC) is Monday 21:30 in São Paulo (UTC-3, no DST since 2019).
        var saoPaulo = TimeZoneInfo.FindSystemTimeZoneById("America/Sao_Paulo");
        var agora = TimeZoneInfo.ConvertTime(new DateTimeOffset(2026, 10, 20, 0, 30, 0, TimeSpan.Zero), saoPaulo).DateTime;
        var segunda = Programacao(new TimeOnly(21, 30), 1800, 1, DiasSemana.Segunda);
        var terca = Programacao(new TimeOnly(0, 0), 3600, 1, DiasSemana.Terca);

        agora.ShouldBe(Em(Segunda, 21, 30, 0));
        AgendaMidia.NoAr([segunda, terca], agora).ShouldBe(segunda);
    }
}
