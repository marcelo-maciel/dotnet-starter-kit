using FluentValidation;
using FSH.Modules.PAF.Contracts.v1.Atendimentos;

namespace FSH.Modules.PAF.Features.v1.Atendimentos.RechamarAtendimento;

public sealed class RechamarAtendimentoCommandValidator : AbstractValidator<RechamarAtendimentoCommand>
{
    public RechamarAtendimentoCommandValidator()
    {
        RuleFor(x => x.AtendimentoId).NotEmpty();
    }
}
