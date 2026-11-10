using FluentValidation;
using FSH.Modules.PAF.Contracts.v1.Atendimentos;

namespace FSH.Modules.PAF.Features.v1.Atendimentos.ChamarFichaEscolhida;

public sealed class ChamarFichaEscolhidaCommandValidator : AbstractValidator<ChamarFichaEscolhidaCommand>
{
    public ChamarFichaEscolhidaCommandValidator()
    {
        RuleFor(x => x.AtendimentoId).NotEmpty();
    }
}
