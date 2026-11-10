using FluentValidation;
using FSH.Modules.PAF.Contracts.v1.Unidades;

namespace FSH.Modules.PAF.Features.v1.Unidades.EnableUnidade;

public sealed class EnableUnidadeCommandValidator : AbstractValidator<EnableUnidadeCommand>
{
    public EnableUnidadeCommandValidator()
    {
        RuleFor(x => x.Id).NotEmpty();
    }
}
