using FluentValidation;
using FSH.Modules.PAF.Contracts.v1.Atendimentos;

namespace FSH.Modules.PAF.Features.v1.Atendimentos.IniciarAtendimento;

public sealed class IniciarAtendimentoCommandValidator : AbstractValidator<IniciarAtendimentoCommand>
{
    public IniciarAtendimentoCommandValidator()
    {
        RuleFor(x => x.AtendimentoId).NotEmpty();
    }
}
