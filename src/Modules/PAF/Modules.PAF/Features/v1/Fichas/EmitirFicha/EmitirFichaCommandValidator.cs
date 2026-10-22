using FluentValidation;
using FSH.Modules.PAF.Contracts.v1.Fichas;
using FSH.Modules.PAF.Domain;

namespace FSH.Modules.PAF.Features.v1.Fichas.EmitirFicha;

public sealed class EmitirFichaCommandValidator : AbstractValidator<EmitirFichaCommand>
{
    public EmitirFichaCommandValidator()
    {
        RuleFor(x => x.EstacaoId).NotEmpty();
        RuleFor(x => x.FilaId).NotEmpty();
        RuleFor(x => x.PrioridadeId).NotEmpty();

        // Without a key a resend after a lost response would issue a second ficha (RF11); never generated server-side.
        RuleFor(x => x.ChaveIdempotencia)
            .NotEmpty().WithMessage("Informe o cabeçalho Idempotency-Key com a chave do ciclo de emissão.")
            .MaximumLength(Ficha.ChaveIdempotenciaMaxLength);
    }
}
