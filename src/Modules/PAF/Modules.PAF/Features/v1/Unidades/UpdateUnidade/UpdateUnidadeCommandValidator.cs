using FluentValidation;
using FSH.Modules.PAF.Contracts.v1.Unidades;

namespace FSH.Modules.PAF.Features.v1.Unidades.UpdateUnidade;

public sealed class UpdateUnidadeCommandValidator : AbstractValidator<UpdateUnidadeCommand>
{
    public UpdateUnidadeCommandValidator()
    {
        RuleFor(x => x.Id).NotEmpty();
        RuleFor(x => x.Nome).NotEmpty().MaximumLength(160);
        RuleFor(x => x.DocumentoFiscal).MaximumLength(32);
    }
}
