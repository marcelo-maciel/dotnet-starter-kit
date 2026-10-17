using Mediator;

namespace FSH.Modules.PAF.Contracts.v1.Estacoes;

/// <summary>Cadastro da própria estação, lido pela credencial dela.</summary>
public sealed record ObterConfiguracaoEstacaoQuery(Guid EstacaoId) : IQuery<ConfiguracaoEstacaoDto>;

public sealed record ConfiguracaoEstacaoDto(Guid EstacaoId, Guid UnidadeId, string Nome, string Tipo, string? Configuracao);
