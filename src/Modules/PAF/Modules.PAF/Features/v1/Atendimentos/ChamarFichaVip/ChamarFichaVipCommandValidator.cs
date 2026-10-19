using FluentValidation;
using FSH.Modules.PAF.Contracts.v1.Atendimentos;

namespace FSH.Modules.PAF.Features.v1.Atendimentos.ChamarFichaVip;

public sealed class ChamarFichaVipCommandValidator : AbstractValidator<ChamarFichaVipCommand>
{
    public ChamarFichaVipCommandValidator()
    {
        RuleFor(x => x.FichaId).NotEmpty();
        RuleFor(x => x.FilaId).NotEmpty();
    }
}
