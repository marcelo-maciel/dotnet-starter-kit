using FluentValidation;
using FSH.Modules.PAF.Contracts.v1.Prioridades;

namespace FSH.Modules.PAF.Features.v1.Prioridades.UpdatePrioridade;

public sealed class UpdatePrioridadeCommandValidator : AbstractValidator<UpdatePrioridadeCommand>
{
    public UpdatePrioridadeCommandValidator()
    {
        RuleFor(x => x.Id).NotEmpty();
        RuleFor(x => x.Nome).NotEmpty().MaximumLength(120);
    }
}
