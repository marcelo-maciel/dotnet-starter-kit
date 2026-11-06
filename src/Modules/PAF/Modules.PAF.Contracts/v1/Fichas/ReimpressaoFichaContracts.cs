using Mediator;

namespace FSH.Modules.PAF.Contracts.v1.Fichas;

/// <summary>
/// RF20 "ficha de espera": the printed senha with its letter (D02), the ficha's operational day and the fila printed
/// on it, in the current unit. All three are required (400); nothing found is 404. Bound from the query string, so
/// they stay nullable and the validator answers the missing one instead of the binder. Answers the stored ficha as
/// printed (never a new number); more than one only when two senhas of the fila print the same text that day.
/// </summary>
public sealed class ReimpressaoFichaQuery : IQuery<IReadOnlyList<FichaEmitidaDto>>
{
    public string? Senha { get; init; }

    public DateOnly? Data { get; init; }

    public Guid? FilaId { get; init; }
}
