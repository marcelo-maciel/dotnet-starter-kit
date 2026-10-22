using FluentValidation;
using FSH.Modules.PAF.Contracts.v1.Atendimentos;

namespace FSH.Modules.PAF.Features.v1.Atendimentos.EncaminharAtendimento;

public sealed class EncaminharAtendimentoCommandValidator : AbstractValidator<EncaminharAtendimentoCommand>
{
    public EncaminharAtendimentoCommandValidator()
    {
        RuleFor(x => x.AtendimentoId).NotEmpty();
        RuleFor(x => x.PrioridadeId).NotEmpty();
        RuleFor(x => x.FilaIds).NotEmpty().WithMessage(EncaminharAtendimentoCommandHandler.MensagemSemFilas);
        RuleForEach(x => x.FilaIds).NotEmpty();

        // A repeated fila would create two waiting atendimentos of one ficha in the same fila.
        RuleFor(x => x.FilaIds)
            .Must(ids => ids.Distinct().Count() == ids.Count)
            .When(x => x.FilaIds is not null)
            .WithMessage("A mesma fila de destino aparece duas vezes.");
    }
}
