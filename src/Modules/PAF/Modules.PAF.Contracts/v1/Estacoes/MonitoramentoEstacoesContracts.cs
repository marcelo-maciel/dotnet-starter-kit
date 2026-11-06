using Mediator;

namespace FSH.Modules.PAF.Contracts.v1.Estacoes;

/// <summary>
/// Um papel de credencial de uma estação (RNF20): a estação combinada "Emissão e Painel" vira duas linhas, para que o
/// painel caído não fique escondido pelo totem conectado. Guichê e administração não têm linha (conectam pela sessão).
/// <paramref name="Aviso"/>: estação ativa, papel sem conexão e unidade em horário de operação (D24).
/// </summary>
public sealed record PapelEstacaoMonitoradoDto(
    Guid UnidadeId,
    string UnidadeNome,
    Guid EstacaoId,
    string EstacaoNome,
    string Tipo,
    bool Ativa,
    string Papel,
    DateTimeOffset? UltimaConexaoEm,
    DateTimeOffset? UltimaQuedaEm,
    bool Conectada,
    bool Aviso);

/// <summary>
/// <paramref name="AgoraEmSaoPaulo"/>: o relógio do PostgreSQL em America/Sao_Paulo usado para o horário de operação.
/// <paramref name="EstadoPeloRegistro"/>: falso quando o registro de conexões (Valkey) não respondeu e o estado saiu só
/// das colunas, que não enxergam a instância que morreu sem desconectar.
/// </summary>
public sealed record MonitoramentoEstacoesDto(
    DateTime AgoraEmSaoPaulo,
    bool EstadoPeloRegistro,
    IReadOnlyList<PapelEstacaoMonitoradoDto> Papeis);

/// <summary>Every station role of the tenant the request targets (Codefi operator, one client per request).</summary>
public sealed record ListEstacoesOperadorQuery : IQuery<MonitoramentoEstacoesDto>;
