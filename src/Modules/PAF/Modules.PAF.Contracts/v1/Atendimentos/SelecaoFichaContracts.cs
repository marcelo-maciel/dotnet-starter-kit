using Mediator;

namespace FSH.Modules.PAF.Contracts.v1.Atendimentos;

// RF04 "selecionar ficha para atendimento" (requisitos seção 4): only on a guichê with the option on.

/// <summary>Atendimentos the session's guichê could call, in the order chamar-próximo would call them: the first one is the next.</summary>
public sealed record ListFichasEmEsperaQuery : IQuery<IReadOnlyList<FichaEmEsperaDto>>;

/// <summary><paramref name="ElegivelDesde"/> is where the wait counts from (requisitos seção 4, "marco do tempo de espera").</summary>
public sealed record FichaEmEsperaDto(Guid AtendimentoId, Guid FichaId, string Senha, Guid FilaId, Guid PrioridadeId, DateTimeOffset ElegivelDesde);

/// <summary>Calls the chosen atendimento on the session's guichê instead of the next one; every condition of the call applies except the order.</summary>
public sealed record ChamarFichaEscolhidaCommand(Guid AtendimentoId) : ICommand<ChamadaDto>, IEscritaDeCadastro;
