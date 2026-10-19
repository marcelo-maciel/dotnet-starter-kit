using Mediator;

namespace FSH.Modules.PAF.Contracts.v1.Atendimentos;

/// <summary>Calls the next ficha on the guichê selected by the session (RF15, seção 4).</summary>
public sealed record ChamarProximaFichaCommand : ICommand<ChamadaDto>, IEscritaDeCadastro;

public sealed record ChamadaDto(Guid AtendimentoId, Guid FichaId, string Senha, Guid FilaId, Guid PrioridadeId, int Versao);

// Each operation names the atendimento it means: a stale click must never act on the one called after it.

/// <summary>Announces the called ficha again (RF21); the first call mark is never rewritten.</summary>
public sealed record RechamarAtendimentoCommand(Guid AtendimentoId) : ICommand<AtendimentoAlteradoDto>, IEscritaDeCadastro;

/// <summary>Chamado → EmAtendimento (RF06: starting is mandatory).</summary>
public sealed record IniciarAtendimentoCommand(Guid AtendimentoId) : ICommand<AtendimentoAlteradoDto>, IEscritaDeCadastro;

/// <summary>EmAtendimento → Encerrado (RF16: only a started atendimento can be closed).</summary>
public sealed record EncerrarAtendimentoCommand(Guid AtendimentoId) : ICommand<AtendimentoAlteradoDto>, IEscritaDeCadastro;

/// <summary>Chamado → Cancelado: how the guichê treats a no-show (RF15, seção 4).</summary>
public sealed record CancelarAtendimentoCommand(Guid AtendimentoId) : ICommand<AtendimentoAlteradoDto>, IEscritaDeCadastro;

/// <summary><paramref name="Estado"/> is the persisted state name after the change.</summary>
public sealed record AtendimentoAlteradoDto(Guid AtendimentoId, string Estado, int Versao);
