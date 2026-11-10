using FluentValidation;
using FSH.Modules.PAF.Contracts.v1.Unidades;

namespace FSH.Modules.PAF.Features.v1.Unidades.CreateUnidade;

public sealed class CreateUnidadeCommandValidator : AbstractValidator<CreateUnidadeCommand>
{
    public CreateUnidadeCommandValidator()
    {
        RuleFor(x => x.Nome).NotEmpty().MaximumLength(160);
        RuleFor(x => x.DocumentoFiscal).MaximumLength(32);
    }
}
