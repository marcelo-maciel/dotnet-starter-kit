using FluentValidation;
using FSH.Modules.PAF.Contracts.v1.Atendimentos;

namespace FSH.Modules.PAF.Features.v1.Atendimentos.EncerrarAtendimento;

public sealed class EncerrarAtendimentoCommandValidator : AbstractValidator<EncerrarAtendimentoCommand>
{
    public EncerrarAtendimentoCommandValidator()
    {
        RuleFor(x => x.AtendimentoId).NotEmpty();
    }
}
