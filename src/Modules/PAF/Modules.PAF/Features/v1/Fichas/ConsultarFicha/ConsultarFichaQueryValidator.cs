using FluentValidation;
using FSH.Modules.PAF.Contracts.v1.Fichas;
using FSH.Modules.PAF.Domain;

namespace FSH.Modules.PAF.Features.v1.Fichas.ConsultarFicha;

public sealed class ConsultarFichaQueryValidator : AbstractValidator<ConsultarFichaQuery>
{
    // Longest printable senha: the longest letter followed by int.MaxValue.
    public static readonly int SenhaMaxLength = FilaPrioridade.LetraMaxLength + int.MaxValue.ToString(System.Globalization.CultureInfo.InvariantCulture).Length;

    public ConsultarFichaQueryValidator()
    {
        RuleFor(x => x.Senha)
            .Must(s => !string.IsNullOrWhiteSpace(s)).WithMessage("Informe a senha impressa na ficha, com a letra.");
        // Separate rule: a When on the chain above would also switch off the presence check.
        RuleFor(x => x.Senha)
            .Must(s => s!.Trim().Length <= SenhaMaxLength && char.IsAsciiDigit(s.Trim()[^1]))
            .When(x => !string.IsNullOrWhiteSpace(x.Senha))
            .WithMessage("A senha é a letra seguida do número, como impressa na ficha.");
        RuleFor(x => x.Data).NotNull().WithMessage("Informe a data da ficha.");
    }
}
