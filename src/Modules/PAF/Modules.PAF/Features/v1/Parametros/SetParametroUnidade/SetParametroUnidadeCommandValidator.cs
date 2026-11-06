using FluentValidation;
using FSH.Modules.PAF.Contracts.v1.Parametros;

namespace FSH.Modules.PAF.Features.v1.Parametros.SetParametroUnidade;

public sealed class SetParametroUnidadeCommandValidator : AbstractValidator<SetParametroUnidadeCommand>
{
    public SetParametroUnidadeCommandValidator()
    {
        RuleFor(x => x.Chave).NotEmpty();
        RuleFor(x => x.Valor).NotNull();
    }
}
