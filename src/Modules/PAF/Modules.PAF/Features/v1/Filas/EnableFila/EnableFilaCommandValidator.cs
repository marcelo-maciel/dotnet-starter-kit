using FluentValidation;
using FSH.Modules.PAF.Contracts.v1.Filas;

namespace FSH.Modules.PAF.Features.v1.Filas.EnableFila;

public sealed class EnableFilaCommandValidator : AbstractValidator<EnableFilaCommand>
{
    public EnableFilaCommandValidator()
    {
        RuleFor(x => x.Id).NotEmpty();
    }
}
