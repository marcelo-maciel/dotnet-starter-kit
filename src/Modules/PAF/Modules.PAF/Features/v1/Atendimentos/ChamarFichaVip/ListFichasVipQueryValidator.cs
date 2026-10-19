using FluentValidation;
using FSH.Modules.PAF.Contracts.v1.Atendimentos;
using FSH.Modules.PAF.Domain;

namespace FSH.Modules.PAF.Features.v1.Atendimentos.ChamarFichaVip;

public sealed class ListFichasVipQueryValidator : AbstractValidator<ListFichasVipQuery>
{
    // The letter (up to LetraMaxLength) followed by an int, which has at most ten digits.
    public const int SenhaMaxLength = FilaPrioridade.LetraMaxLength + 10;

    public ListFichasVipQueryValidator()
    {
        RuleFor(x => x.Senha).Must(s => !string.IsNullOrWhiteSpace(s)).WithMessage("Informe a senha impressa na ficha.");
        // Separate rule: a When on the chain above would also switch off the presence check.
        RuleFor(x => x.Senha).Must(s => s!.Trim().Length <= SenhaMaxLength)
            .When(x => !string.IsNullOrWhiteSpace(x.Senha))
            .WithMessage($"A senha tem no máximo {SenhaMaxLength} caracteres.");
        RuleFor(x => x.Data).NotNull().NotEqual(default(DateOnly)).WithMessage("Informe a data da ficha.");
    }
}
