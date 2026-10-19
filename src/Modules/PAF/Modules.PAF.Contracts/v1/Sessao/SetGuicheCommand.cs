using Mediator;

namespace FSH.Modules.PAF.Contracts.v1.Sessao;

/// <summary>
/// Liga a sessão a um guichê da unidade corrente, soltando o anterior no mesmo ato (ADR 0006). Com atendimento
/// pendente de outro usuário no guichê, a seleção só passa com <paramref name="LiberarPendencia"/>, que cancela
/// esse atendimento na mesma transação (RF04, RF12); o próprio usuário retoma a pendência sem liberar nada.
/// </summary>
public sealed record SetGuicheCommand(Guid EstacaoId, bool LiberarPendencia) : ICommand, IEscritaDeCadastro;
