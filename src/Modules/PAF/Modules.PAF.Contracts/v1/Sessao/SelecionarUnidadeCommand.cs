using Mediator;

namespace FSH.Modules.PAF.Contracts.v1.Sessao;

/// <summary>Troca a unidade corrente da sessão por outra do escopo do usuário (ADR 0006).</summary>
public sealed record SelecionarUnidadeCommand(Guid UnidadeId) : ICommand;
