using FluentValidation;
using FSH.Modules.PAF.Contracts.v1.Fichas;

namespace FSH.Modules.PAF.Features.v1.Fichas.CancelarAtendimentoDaFicha;

public sealed class CancelarAtendimentoDaFichaCommandValidator : AbstractValidator<CancelarAtendimentoDaFichaCommand>
{
    public CancelarAtendimentoDaFichaCommandValidator()
    {
        RuleFor(x => x.AtendimentoId).NotEmpty();
    }
}
