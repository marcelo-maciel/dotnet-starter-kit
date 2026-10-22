using Mediator;

namespace FSH.Modules.PAF.Contracts.v1.Atendimentos;

/// <summary>
/// Cancelado → Aguardando on the same row (RF12 "reabilitar"), outside the guichê: the way back for a no-show
/// who returned and for an abandoned atendimento released by cancelling it.
/// </summary>
public sealed record ReabilitarAtendimentoDaFichaCommand(Guid AtendimentoId) : ICommand<AtendimentoAlteradoDto>, IEscritaDeCadastro;
