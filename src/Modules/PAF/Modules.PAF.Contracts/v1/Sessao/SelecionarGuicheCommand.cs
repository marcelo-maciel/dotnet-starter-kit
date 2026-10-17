using Mediator;

namespace FSH.Modules.PAF.Contracts.v1.Sessao;

/// <summary>Liga a sessão a um guichê da unidade corrente, soltando o anterior no mesmo ato (ADR 0006).</summary>
public sealed record SelecionarGuicheCommand(Guid EstacaoId) : ICommand;
