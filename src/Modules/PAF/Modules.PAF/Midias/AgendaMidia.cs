using FSH.Modules.PAF.Data;
using FSH.Modules.PAF.Domain;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.PAF.Midias;

/// <summary>A programação as the painel sees it today: its window already computed from the mídia's current duration.</summary>
public sealed record ProgramacaoDoDia(
    Guid Id,
    Guid MidiaId,
    string Nome,
    DateOnly DataInicial,
    DateOnly DataFinal,
    TimeOnly HoraInicial,
    int Repeticoes,
    DiasSemana Dias,
    int TempoMidiaSegundos,
    DateTimeOffset CriadoEm)
{
    public long TempoTotalSegundos => AgendaMidia.TempoTotalSegundos(TempoMidiaSegundos, Repeticoes);

    public long HoraFinalSegundos => AgendaMidia.HoraFinalSegundos(HoraInicial, TempoMidiaSegundos, Repeticoes);
}

/// <summary>Today's programações of the current unidade and the one on air, both at the database's São Paulo clock.</summary>
public sealed record AgendaDeHoje(DateTime AgoraSaoPaulo, IReadOnlyList<ProgramacaoDoDia> Programacoes)
{
    public ProgramacaoDoDia? NoAr => AgendaMidia.NoAr(Programacoes, AgoraSaoPaulo);
}

/// <summary>
/// RF10 time rules, in one place for the cadastro (M05) and the painel (M06, M08). The window of a programação is
/// [HoraInicial, HoraInicial + duração × repetições), São Paulo wall clock, on the days of its mask inside its period.
/// D13: the cadastro refuses an end after midnight, and among overlapping programações the latest start wins until it
/// ends.
/// </summary>
public static class AgendaMidia
{
    public const long SegundosNoDia = 86_400;

    public static long TempoTotalSegundos(int tempoMidiaSegundos, int repeticoes) => (long)tempoMidiaSegundos * repeticoes;

    /// <summary>Seconds from midnight; 86400 ends exactly at midnight.</summary>
    public static long HoraFinalSegundos(TimeOnly horaInicial, int tempoMidiaSegundos, int repeticoes) =>
        SegundosDoDia(horaInicial) + TempoTotalSegundos(tempoMidiaSegundos, repeticoes);

    /// <summary>D13: a programação ends by midnight of the day it starts, like the fila's window.</summary>
    public static bool TerminaAteAMeiaNoite(TimeOnly horaInicial, int tempoMidiaSegundos, int repeticoes) =>
        HoraFinalSegundos(horaInicial, tempoMidiaSegundos, repeticoes) <= SegundosNoDia;

    public static DiasSemana DiaDaSemana(DateOnly dia) => (DiasSemana)(1 << (int)dia.DayOfWeek);

    public static bool ValeNoDia(ProgramacaoDoDia programacao, DateOnly dia)
    {
        ArgumentNullException.ThrowIfNull(programacao);
        return programacao.DataInicial <= dia
            && dia <= programacao.DataFinal
            && (programacao.Dias & DiaDaSemana(dia)) != 0;
    }

    /// <summary>
    /// The single "on air now" rule. <paramref name="agoraSaoPaulo"/> must come from <see cref="PafRelogio"/>, never
    /// from the C# clock. A programação whose end a later duration edit pushed past midnight plays until midnight and
    /// never spills into the next day. Ties on the start go to the newest programação, so the pick is deterministic.
    /// </summary>
    public static ProgramacaoDoDia? NoAr(IEnumerable<ProgramacaoDoDia> programacoes, DateTime agoraSaoPaulo)
    {
        ArgumentNullException.ThrowIfNull(programacoes);
        var hoje = DateOnly.FromDateTime(agoraSaoPaulo);
        var agora = (long)agoraSaoPaulo.TimeOfDay.TotalSeconds;
        return programacoes
            .Where(p => ValeNoDia(p, hoje)
                && SegundosDoDia(p.HoraInicial) <= agora
                && agora < p.HoraFinalSegundos)
            .OrderByDescending(p => p.HoraInicial)
            .ThenByDescending(p => p.CriadoEm)
            .ThenByDescending(p => p.Id)
            .FirstOrDefault();
    }

    /// <summary>
    /// Today's active programações of the current unidade (unit filter) whose mídia is active and confirmed, read with
    /// the database's today and now in São Paulo.
    /// </summary>
    public static async Task<AgendaDeHoje> DeHojeAsync(PafDbContext db, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        var agora = await PafRelogio.AgoraEmSaoPauloAsync(db, ct).ConfigureAwait(false);
        var hoje = DateOnly.FromDateTime(agora);
        var dia = DiaDaSemana(hoje);
        var programacoes = await (
                from p in db.ProgramacoesMidia.AsNoTracking()
                join m in db.Midias on p.MidiaId equals m.Id
                join e in db.EnviosMidia on m.EnvioId equals e.Id
                where p.Ativa && m.Ativa && e.ConfirmadoEm != null
                    && p.DataInicial <= hoje && hoje <= p.DataFinal
                    && (p.Dias & dia) != 0
                orderby p.HoraInicial, p.Id
                select new ProgramacaoDoDia(
                    p.Id, p.MidiaId, p.Nome, p.DataInicial, p.DataFinal, p.HoraInicial, p.Repeticoes, p.Dias,
                    m.DuracaoSegundos, p.CriadoEm))
            .ToListAsync(ct)
            .ConfigureAwait(false);
        return new AgendaDeHoje(agora, programacoes);
    }

    private static long SegundosDoDia(TimeOnly hora) => hora.Ticks / TimeSpan.TicksPerSecond;
}
