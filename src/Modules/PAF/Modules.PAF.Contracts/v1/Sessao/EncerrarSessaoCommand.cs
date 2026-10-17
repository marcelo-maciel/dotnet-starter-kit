using Mediator;

namespace FSH.Modules.PAF.Contracts.v1.Sessao;

/// <summary>Logout pelo servidor: revoga a sessão do kit e solta licença e guichê (seção 5).</summary>
public sealed record EncerrarSessaoCommand : ICommand;
