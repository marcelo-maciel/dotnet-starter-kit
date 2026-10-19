using FluentValidation;
using FSH.Modules.PAF.Contracts.v1.Parametros;

namespace FSH.Modules.PAF.Features.v1.Parametros.RemoveParametroUnidade;

public sealed class RemoveParametroUnidadeCommandValidator : AbstractValidator<RemoveParametroUnidadeCommand>
{
    public RemoveParametroUnidadeCommandValidator()
    {
        RuleFor(x => x.Chave).NotEmpty();
    }
}
