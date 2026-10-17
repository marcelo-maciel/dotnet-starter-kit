using Mediator;

namespace FSH.Modules.PAF.Contracts.v1.Painel;

/// <summary>State the painel re-queries on connect and reconnect (RNF03); the station comes from its credential.</summary>
public sealed record ObterEstadoPainelQuery(Guid EstacaoId) : IQuery<EstadoPainelDto>;

/// <summary>Latest calls of the painel's filas, most recent call or recall first.</summary>
public sealed record EstadoPainelDto(IReadOnlyList<ChamadaPainelDto> Chamadas);

/// <summary>
/// <paramref name="Versao"/> lets the screen drop any later event with a version not above it (seção 7).
/// <paramref name="Estado"/> is the persisted state name.
/// </summary>
public sealed record ChamadaPainelDto(
    Guid AtendimentoId,
    Guid FichaId,
    int Versao,
    string Senha,
    string Estado,
    Guid EstacaoId,
    string NomeEstacao,
    DateTimeOffset ChamadoEm,
    DateTimeOffset? UltimaRechamadaEm);
