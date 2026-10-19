using FluentValidation;
using FSH.Modules.PAF.Contracts.v1.Fichas;
using FSH.Modules.PAF.Features.v1.Fichas.ConsultarFicha;

namespace FSH.Modules.PAF.Features.v1.Fichas.ReimprimirFichaDeEspera;

public sealed class ReimpressaoFichaQueryValidator : AbstractValidator<ReimpressaoFichaQuery>
{
    public ReimpressaoFichaQueryValidator()
    {
        RuleFor(x => x.Senha)
            .Must(s => !string.IsNullOrWhiteSpace(s)).WithMessage("Informe a senha impressa na ficha, com a letra.");
        // Separate rule: a When on the chain above would also switch off the presence check.
        RuleFor(x => x.Senha)
            .Must(s => s!.Trim().Length <= ConsultarFichaQueryValidator.SenhaMaxLength && char.IsAsciiDigit(s.Trim()[^1]))
            .When(x => !string.IsNullOrWhiteSpace(x.Senha))
            .WithMessage("A senha é a letra seguida do número, como impressa na ficha.");
        RuleFor(x => x.Data).NotNull().WithMessage("Informe a data da ficha.");
        RuleFor(x => x.FilaId)
            .Must(f => f is { } id && id != Guid.Empty).WithMessage("Informe a fila impressa na ficha.");
    }
}
