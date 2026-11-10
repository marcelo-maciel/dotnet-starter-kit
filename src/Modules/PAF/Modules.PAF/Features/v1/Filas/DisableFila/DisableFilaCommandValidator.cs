using FluentValidation;
using FSH.Modules.PAF.Contracts.v1.Filas;

namespace FSH.Modules.PAF.Features.v1.Filas.DisableFila;

public sealed class DisableFilaCommandValidator : AbstractValidator<DisableFilaCommand>
{
    public DisableFilaCommandValidator()
    {
        RuleFor(x => x.Id).NotEmpty();
    }
}
