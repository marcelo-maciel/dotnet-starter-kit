using Mediator;

namespace FSH.Modules.PAF.Contracts.v1.Fichas;

// RF13 "fichas em atendimento": the detection instrument of the abandoned atendimento (requisitos seção 4).

/// <summary>
/// Chamado and EmAtendimento atendimentos of the current unit, with no date filter (an abandoned one crosses midnight),
/// longest stay first. <paramref name="FilaId"/> null = every fila; a fila outside the current unit is 404.
/// </summary>
public sealed record ListFichasEmAtendimentoQuery(Guid? FilaId) : IQuery<FichasEmAtendimentoDto>;

/// <summary><paramref name="Filas"/> are the current unit's filas, for the screen's filter (no Filas.View needed).</summary>
public sealed record FichasEmAtendimentoDto(int Total, IReadOnlyList<FichaEmAtendimentoDto> Atendimentos, IReadOnlyList<FilaDoFiltroDto> Filas);

/// <summary>
/// <paramref name="Prioridade"/> is RF13's "tipo". <paramref name="ChamadoEm"/> is the entry time and
/// <paramref name="PermanenciaSegundos"/> counts from it on the PostgreSQL clock.
/// </summary>
public sealed record FichaEmAtendimentoDto(
    Guid AtendimentoId,
    Guid FichaId,
    string Senha,
    Guid FilaId,
    string Fila,
    string Prioridade,
    Guid? EstacaoId,
    string? Guiche,
    string Estado,
    DateTimeOffset ChamadoEm,
    DateTimeOffset? IniciadoEm,
    long PermanenciaSegundos,
    int Versao);

public sealed record FilaDoFiltroDto(Guid Id, string Nome);
