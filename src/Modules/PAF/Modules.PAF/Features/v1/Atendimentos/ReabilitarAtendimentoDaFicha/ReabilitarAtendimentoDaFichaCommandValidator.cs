using FluentValidation;
using FSH.Modules.PAF.Contracts.v1.Atendimentos;

namespace FSH.Modules.PAF.Features.v1.Atendimentos.ReabilitarAtendimentoDaFicha;

public sealed class ReabilitarAtendimentoDaFichaCommandValidator : AbstractValidator<ReabilitarAtendimentoDaFichaCommand>
{
    public ReabilitarAtendimentoDaFichaCommandValidator()
    {
        RuleFor(x => x.AtendimentoId).NotEmpty();
    }
}
