using Mediator;

namespace FSH.Modules.PAF.Contracts.v1.Atendimentos;

// RF19 "atendimento VIP" (D01, D02): the guichê calls a named ficha out of the queue order, under Atendimentos.Vip.

/// <summary>
/// The VIP search (owner decision 19/10/2026): every ficha of the current unit whose printed senha, with its letter
/// (D02), and operational date match. A letter may hold digits, so two fichas can print the same senha; all are
/// listed for the attendant to pick, and none is an empty list. Bound from the query string, so both stay nullable
/// and the validator answers the missing one instead of the binder.
/// </summary>
public sealed class ListFichasVipQuery : IQuery<IReadOnlyList<FichaVipDto>>
{
    public string? Senha { get; init; }

    public DateOnly? Data { get; init; }
}

/// <summary>One match of the VIP search, with what tells two equal senhas apart: emission time, fila and prioridade.</summary>
public sealed record FichaVipDto(Guid FichaId, string Senha, DateTimeOffset EmitidaEm, string Fila, string Prioridade);

/// <summary>
/// Calls the ficha the attendant picked from the VIP search. <paramref name="FilaId"/> picks the ficha's waiting
/// atendimento when forwarding left it in more than one fila. Whether it is still callable follows the atendimento's
/// own eligibility date (RF19, 03/10/2026).
/// </summary>
public sealed record ChamarFichaVipCommand(Guid FichaId, Guid FilaId) : ICommand<ChamadaDto>, IEscritaDeCadastro;

/// <summary>The filas the session's guichê serves, the only ones a VIP call can reach.</summary>
public sealed record ListFilasVipQuery : IQuery<IReadOnlyList<FilaVipDto>>;

public sealed record FilaVipDto(Guid Id, string Nome);
