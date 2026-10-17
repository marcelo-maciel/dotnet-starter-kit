using FluentValidation;
using FSH.Modules.PAF.Contracts.v1.Prioridades;

namespace FSH.Modules.PAF.Features.v1.Prioridades.CreatePrioridade;

public sealed class CreatePrioridadeCommandValidator : AbstractValidator<CreatePrioridadeCommand>
{
    public CreatePrioridadeCommandValidator()
    {
        RuleFor(x => x.Nome).NotEmpty().MaximumLength(120);
    }
}
