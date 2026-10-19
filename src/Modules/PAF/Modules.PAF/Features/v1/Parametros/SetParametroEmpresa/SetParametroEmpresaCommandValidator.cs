using FluentValidation;
using FSH.Modules.PAF.Contracts.v1.Parametros;

namespace FSH.Modules.PAF.Features.v1.Parametros.SetParametroEmpresa;

public sealed class SetParametroEmpresaCommandValidator : AbstractValidator<SetParametroEmpresaCommand>
{
    public SetParametroEmpresaCommandValidator()
    {
        RuleFor(x => x.Chave).NotEmpty();
        RuleFor(x => x.Valor).NotNull();
    }
}
