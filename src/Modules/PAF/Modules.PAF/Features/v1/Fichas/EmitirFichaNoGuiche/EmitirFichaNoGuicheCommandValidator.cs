using FluentValidation;
using FSH.Modules.PAF.Contracts.v1.Fichas;
using FSH.Modules.PAF.Domain;

namespace FSH.Modules.PAF.Features.v1.Fichas.EmitirFichaNoGuiche;

public sealed class EmitirFichaNoGuicheCommandValidator : AbstractValidator<EmitirFichaNoGuicheCommand>
{
    public EmitirFichaNoGuicheCommandValidator()
    {
        RuleFor(x => x.FilaId).NotEmpty();
        RuleFor(x => x.PrioridadeId).NotEmpty();

        // Without a key a resend after a lost response would issue a second ficha (RF11); never generated server-side.
        RuleFor(x => x.ChaveIdempotencia)
            .NotEmpty().WithMessage("Informe o cabeçalho Idempotency-Key com a chave da emissão.")
            .MaximumLength(Ficha.ChaveIdempotenciaMaxLength);
    }
}
