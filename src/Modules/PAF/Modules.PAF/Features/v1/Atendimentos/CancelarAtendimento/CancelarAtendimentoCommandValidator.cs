using FluentValidation;
using FSH.Modules.PAF.Contracts.v1.Atendimentos;

namespace FSH.Modules.PAF.Features.v1.Atendimentos.CancelarAtendimento;

public sealed class CancelarAtendimentoCommandValidator : AbstractValidator<CancelarAtendimentoCommand>
{
    public CancelarAtendimentoCommandValidator()
    {
        RuleFor(x => x.AtendimentoId).NotEmpty();
    }
}
