using FSH.Modules.PAF.Contracts.v1.Atendimentos;
using Mediator;

namespace FSH.Modules.PAF.Contracts.v1.Fichas;

/// <summary>
/// RF12 cancel from the fichas screens, outside the guichê: Aguardando, Chamado or EmAtendimento → Cancelado, in the
/// current unit. Encerrado and Cancelado answer 409.
/// </summary>
public sealed record CancelarAtendimentoDaFichaCommand(Guid AtendimentoId) : ICommand<AtendimentoAlteradoDto>, IEscritaDeCadastro;
