using Mediator;

namespace FSH.Modules.PAF.Contracts.v1.Filas;

public sealed record FilaPrioridadeDto(Guid Id, Guid PrioridadeId, string Letra, int NumeroInicial, int Ordem);

/// <summary><paramref name="CombinacoesSemCobertura"/>: ids of the <see cref="FilaPrioridadeDto"/> rows that no active guichê covers (RF02).</summary>
public sealed record FilaDto(
    Guid Id,
    Guid UnidadeId,
    string Nome,
    string NomeExibicao,
    TimeOnly? HoraInicial,
    TimeOnly? HoraFinal,
    bool Ativa,
    IReadOnlyList<FilaPrioridadeDto> Prioridades,
    IReadOnlyList<Guid> CombinacoesSemCobertura);

/// <summary>
/// A fila x prioridade combination (the <see cref="FilaPrioridadeDto"/> row) named for a warning. Covered means an
/// active station with the fila in its guichê role and the prioridade enabled (RF02, 03/10/2026).
/// </summary>
public sealed record CombinacaoFilaPrioridadeDto(Guid Id, Guid FilaId, string Fila, Guid PrioridadeId, string Prioridade, string Letra);

/// <summary>One line of the fila's priority grid (RF02): the senha letter is unique in the whole unidade.</summary>
public sealed record FilaPrioridadeItem(Guid PrioridadeId, string Letra, int NumeroInicial, int Ordem);

/// <summary>The fila is born with its grid: a fila without any prioridade is refused (RF04, "estação muda").</summary>
public sealed record CreateFilaCommand(
    string Nome,
    string NomeExibicao,
    TimeOnly? HoraInicial,
    TimeOnly? HoraFinal,
    IReadOnlyList<FilaPrioridadeItem> Prioridades) : ICommand<Guid>;

public sealed record UpdateFilaCommand(
    Guid Id,
    string Nome,
    string NomeExibicao,
    TimeOnly? HoraInicial,
    TimeOnly? HoraFinal) : ICommand<Guid>;

public sealed record EnableFilaCommand(Guid Id) : ICommand<Guid>;

public sealed record DisableFilaCommand(Guid Id) : ICommand<Guid>;

public sealed record AddFilaPrioridadeCommand(Guid FilaId, Guid PrioridadeId, string Letra, int NumeroInicial, int Ordem) : ICommand<Guid>;

public sealed record UpdateFilaPrioridadeCommand(Guid FilaId, Guid PrioridadeId, string Letra, int NumeroInicial, int Ordem) : ICommand<Guid>;

/// <summary>Filas of the current unidade only.</summary>
public sealed record ListFilasQuery : IQuery<IReadOnlyList<FilaDto>>;

/// <summary>
/// Filas of every unidade in the caller's scope, in one read (ADR 0003, explicit aggregation path):
/// only the unit filter is turned off, the tenant filter stays.
/// </summary>
public sealed record ListFilasConsolidadasQuery : IQuery<IReadOnlyList<FilaDto>>;
