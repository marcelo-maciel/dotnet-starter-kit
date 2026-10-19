using Mediator;

namespace FSH.Modules.PAF.Contracts.v1.Fichas;

/// <summary>
/// RF18 "consultar ficha": the printed senha with its letter (D02) and the ficha's operational day, in the current
/// unit. Both are required (400); nothing found is 404. Bound from the query string, so both stay nullable and the
/// validator answers the missing one instead of the binder.
/// </summary>
public sealed class ConsultarFichaQuery : IQuery<IReadOnlyList<FichaConsultadaDto>>
{
    public string? Senha { get; init; }

    public DateOnly? Data { get; init; }
}

/// <summary>
/// One ficha with every atendimento it ever had: the emission one and the forwarded ones, each pointing to its
/// origin. More than one ficha only when a letter changed hands during the day and two senhas print the same.
/// </summary>
public sealed record FichaConsultadaDto(
    Guid FichaId,
    string Senha,
    DateOnly DataOperacional,
    DateTimeOffset CriadoEm,
    string Fila,
    string Prioridade,
    IReadOnlyList<AtendimentoDaFichaDto> Atendimentos);

/// <summary>
/// <c>FimEm</c> is the close or the cancel. <c>TempoTotalSegundos</c> runs from the start of the service to its end,
/// or to the database's now() while still in service; null when the service never started.
/// </summary>
public sealed record AtendimentoDaFichaDto(
    Guid AtendimentoId,
    Guid FilaId,
    string Fila,
    string Prioridade,
    string Estado,
    string? Guiche,
    DateTimeOffset CriadoEm,
    DateTimeOffset? ChamadoEm,
    DateTimeOffset? IniciadoEm,
    DateTimeOffset? FimEm,
    long? TempoTotalSegundos,
    Guid? AtendimentoOrigemId,
    int Versao);
