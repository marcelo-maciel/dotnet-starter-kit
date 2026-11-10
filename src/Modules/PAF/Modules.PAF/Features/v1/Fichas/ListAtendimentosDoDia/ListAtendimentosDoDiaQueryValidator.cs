using FluentValidation;
using FSH.Modules.PAF.Contracts.v1.Fichas;
using FSH.Modules.PAF.Domain;

namespace FSH.Modules.PAF.Features.v1.Fichas.ListAtendimentosDoDia;

public sealed class ListAtendimentosDoDiaQueryValidator : AbstractValidator<ListAtendimentosDoDiaQuery>
{
    public ListAtendimentosDoDiaQueryValidator()
    {
        // Exact state names only: a number or a near miss would otherwise parse into some state.
        RuleFor(x => x.Estado)
            .Must(e => e is null || Enum.GetNames<EstadoAtendimento>().Contains(e, StringComparer.Ordinal))
            .WithMessage($"Estado deve ser um de: {string.Join(", ", Enum.GetNames<EstadoAtendimento>())}.");
        RuleFor(x => x.FilaId).Must(f => f != Guid.Empty).When(x => x.FilaId is not null);
    }
}
