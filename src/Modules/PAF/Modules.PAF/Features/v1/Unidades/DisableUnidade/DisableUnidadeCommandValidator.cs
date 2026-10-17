using FluentValidation;
using FSH.Modules.PAF.Contracts.v1.Unidades;

namespace FSH.Modules.PAF.Features.v1.Unidades.DisableUnidade;

public sealed class DisableUnidadeCommandValidator : AbstractValidator<DisableUnidadeCommand>
{
    public DisableUnidadeCommandValidator()
    {
        RuleFor(x => x.Id).NotEmpty();
    }
}
