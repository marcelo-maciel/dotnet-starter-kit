using Mediator;

namespace FSH.Modules.PAF.Contracts.v1.Fichas;

// RF12 "fichas do dia": one line per atendimento of the current unit, never across units.

/// <summary>
/// <paramref name="Data"/> is the atendimento's <c>DataOperacional</c> (America/Sao_Paulo); null = today by the
/// PostgreSQL clock. <paramref name="Estado"/> is an <c>EstadoAtendimento</c> name; null = every state.
/// </summary>
public sealed record ListAtendimentosDoDiaQuery(DateOnly? Data, Guid? FilaId, string? Estado) : IQuery<FichasDoDiaDto>;

/// <summary>
/// <paramref name="TotalAguardando"/> counts the Aguardando atendimentos of the same date and fila, whatever the
/// estado filter. <paramref name="Truncada"/> = more lines than the cap matched; the newest are returned.
/// <paramref name="Filas"/> are the filas of the current unit, for the filter, so the screen needs no other permission.
/// </summary>
public sealed record FichasDoDiaDto(
    DateOnly Data,
    int TotalAguardando,
    bool Truncada,
    IReadOnlyList<AtendimentoDoDiaDto> Atendimentos,
    IReadOnlyList<FilaDasFichasDto> Filas);

/// <summary>
/// <paramref name="ChegadaEm"/> is the ficha's emission; <paramref name="EsperaSegundos"/> is computed by PostgreSQL
/// from <paramref name="ElegivelDesde"/> to the call (or the cancel, or now while waiting). Guichê fields are null
/// while no guichê holds or held the atendimento.
/// </summary>
public sealed record AtendimentoDoDiaDto(
    Guid AtendimentoId,
    Guid FichaId,
    string Senha,
    Guid FilaId,
    string Fila,
    Guid PrioridadeId,
    string Prioridade,
    string Estado,
    int Versao,
    DateTimeOffset ChegadaEm,
    DateTimeOffset ElegivelDesde,
    long EsperaSegundos,
    Guid? EstacaoId,
    string? Guiche,
    int? NumeroGuiche,
    string? NomeExibicaoGuiche,
    bool Encaminhado);

public sealed record FilaDasFichasDto(Guid Id, string NomeExibicao, bool Ativa);

/// <summary>Reprint (RF11, RF12): the stored ficha as the totem printed it; never a new number.</summary>
public sealed record ObterImpressaoFichaQuery(Guid FichaId) : IQuery<FichaEmitidaDto>;
