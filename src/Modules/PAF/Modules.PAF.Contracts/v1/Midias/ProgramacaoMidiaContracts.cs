using FSH.Framework.Shared.Persistence;
using Mediator;

namespace FSH.Modules.PAF.Contracts.v1.Midias;

/// <summary>
/// Programação de mídia of the current unidade (RF10). Dates and <paramref name="HoraInicial"/> are São Paulo wall
/// clock. <paramref name="Dias"/> is the weekday bit mask in <c>DayOfWeek</c> order (1 = domingo, 2 = segunda … 64 =
/// sábado, 127 = todos os dias). Media time, total time and end are computed on read from the mídia's current
/// duration, never stored: <paramref name="HoraFinalSegundos"/> counts seconds from midnight, so 86400 is a
/// programação that ends exactly at midnight (a <c>TimeOnly</c> cannot say 24:00).
/// </summary>
public sealed record ProgramacaoMidiaDto(
    Guid Id,
    Guid UnidadeId,
    Guid MidiaId,
    string MidiaDescricao,
    bool MidiaAtiva,
    string Nome,
    DateOnly DataInicial,
    DateOnly DataFinal,
    TimeOnly HoraInicial,
    int Repeticoes,
    int Dias,
    bool Ativa,
    int Versao,
    int TempoMidiaSegundos,
    long TempoTotalSegundos,
    long HoraFinalSegundos);

/// <summary>Creates a programação, active, for an active and confirmed mídia of the current unidade.</summary>
public sealed record CreateProgramacaoMidiaCommand(
    Guid MidiaId,
    string Nome,
    DateOnly DataInicial,
    DateOnly DataFinal,
    TimeOnly HoraInicial,
    int Repeticoes,
    int Dias) : ICommand<ProgramacaoMidiaDto>, IEscritaDeCadastro;

/// <summary>Replaces every field; <paramref name="Versao"/> is the one the client read (409 when it moved).</summary>
public sealed record UpdateProgramacaoMidiaCommand(
    Guid Id,
    Guid MidiaId,
    string Nome,
    DateOnly DataInicial,
    DateOnly DataFinal,
    TimeOnly HoraInicial,
    int Repeticoes,
    int Dias,
    int Versao) : ICommand<ProgramacaoMidiaDto>, IEscritaDeCadastro;

public sealed record EnableProgramacaoMidiaCommand(Guid Id) : ICommand<ProgramacaoMidiaDto>, IEscritaDeCadastro;

public sealed record DisableProgramacaoMidiaCommand(Guid Id) : ICommand<ProgramacaoMidiaDto>, IEscritaDeCadastro;

/// <summary>Physical deletion (RF10): the row is gone, so the painel stops showing it at its next read.</summary>
public sealed record DeleteProgramacaoMidiaCommand(Guid Id) : ICommand, IEscritaDeCadastro;

/// <summary>
/// Search of the current unidade's programações (RF10), every filter optional and combined by AND: <see cref="Nome"/>
/// contains (case-insensitive); <see cref="Dias"/> any of the mask's days; <see cref="Hora"/> inside
/// [HoraInicial, hora final) of the computed end; <see cref="De"/>/<see cref="Ate"/> period overlapping
/// [DataInicial, DataFinal], inclusive. Ordered by name, start hour and id; <see cref="Sort"/> is not read.
/// </summary>
public sealed class SearchProgramacoesMidiaQuery : IPagedQuery, IQuery<PagedResponse<ProgramacaoMidiaDto>>
{
    public string? Nome { get; set; }

    public int? Dias { get; set; }

    public TimeOnly? Hora { get; set; }

    public DateOnly? De { get; set; }

    public DateOnly? Ate { get; set; }

    public int? PageNumber { get; set; }

    public int? PageSize { get; set; }

    public string? Sort { get; set; }
}
