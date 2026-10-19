using Mediator;

namespace FSH.Modules.PAF.Contracts.v1.Atendimentos;

// RF19 "atendimento VIP" (D01, D02): the guichê calls a named ficha out of the queue order, under Atendimentos.Vip.

/// <summary>
/// <paramref name="Senha"/> is the printed senha with its letter (D02), unique in the unit for a day;
/// <paramref name="Data"/> is the ficha's operational date and only identifies it: whether the atendimento is still
/// callable follows its own eligibility date (RF19, 03/10/2026). <paramref name="FilaId"/> picks the ficha's waiting
/// atendimento when forwarding left it in more than one fila.
/// </summary>
public sealed record ChamarFichaVipCommand(string Senha, DateOnly Data, Guid FilaId) : ICommand<ChamadaDto>, IEscritaDeCadastro;

/// <summary>The filas the session's guichê serves, the only ones a VIP call can reach.</summary>
public sealed record ListFilasVipQuery : IQuery<IReadOnlyList<FilaVipDto>>;

public sealed record FilaVipDto(Guid Id, string Nome);
