using FluentValidation;
using FSH.Modules.PAF.Contracts.v1.Prioridades;

namespace FSH.Modules.PAF.Features.v1.Prioridades.DisablePrioridade;

public sealed class DisablePrioridadeCommandValidator : AbstractValidator<DisablePrioridadeCommand>
{
    public DisablePrioridadeCommandValidator()
    {
        RuleFor(x => x.Id).NotEmpty();
    }
}
