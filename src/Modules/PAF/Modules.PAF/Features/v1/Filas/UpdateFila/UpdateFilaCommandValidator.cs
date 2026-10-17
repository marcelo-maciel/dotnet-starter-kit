using FluentValidation;
using FSH.Modules.PAF.Contracts.v1.Filas;

namespace FSH.Modules.PAF.Features.v1.Filas.UpdateFila;

public sealed class UpdateFilaCommandValidator : AbstractValidator<UpdateFilaCommand>
{
    public UpdateFilaCommandValidator()
    {
        RuleFor(x => x.Id).NotEmpty();
        RuleFor(x => x.Nome).NotEmpty().MaximumLength(120);
        RuleFor(x => x.NomeExibicao).NotEmpty().MaximumLength(120);
        RuleFor(x => x).Must(x => FilaValidation.JanelaValida(x.HoraInicial, x.HoraFinal))
            .WithName("HoraFinal")
            .WithMessage(FilaValidation.MensagemJanela);
    }
}
