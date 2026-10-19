using Mediator;

namespace FSH.Modules.PAF.Contracts.v1.Estacoes;

/// <summary>Cadastro da própria estação, lido pela credencial dela.</summary>
public sealed record ObterConfiguracaoEstacaoQuery(Guid EstacaoId) : IQuery<ConfiguracaoEstacaoDto>;

/// <summary><paramref name="Botoes"/>: botões do totem, filas do papel emissão da estação com as prioridades ativas delas, já na ordem de exibição. Vazio para o painel.</summary>
public sealed record ConfiguracaoEstacaoDto(
    Guid EstacaoId, Guid UnidadeId, string Nome, string Tipo, string? Configuracao, IReadOnlyList<BotaoEmissaoDto> Botoes);

/// <summary>
/// Um botão do totem (RF04, bloco Emissão de Fichas): fila e prioridade emitidas juntas, com a letra da senha, mais a
/// aparência cadastrada na fila (RF02); cor nula = padrão da estação.
/// </summary>
public sealed record BotaoEmissaoDto(
    Guid FilaId,
    string Fila,
    Guid PrioridadeId,
    string Prioridade,
    string Letra,
    int Ordem,
    string? Fonte,
    string? CorFonte,
    string? CorBotao,
    string? CorBotaoSelecionado);
