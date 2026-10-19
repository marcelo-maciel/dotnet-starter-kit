using FluentValidation;
using FSH.Modules.PAF.Contracts.v1.Atendimentos;
using FSH.Modules.PAF.Domain;

namespace FSH.Modules.PAF.Features.v1.Atendimentos.ChamarFichaVip;

public sealed class ChamarFichaVipCommandValidator : AbstractValidator<ChamarFichaVipCommand>
{
    // The letter (up to LetraMaxLength) followed by an int, which has at most ten digits.
    public const int SenhaMaxLength = FilaPrioridade.LetraMaxLength + 10;

    public ChamarFichaVipCommandValidator()
    {
        RuleFor(x => x.Senha).NotEmpty().Must(s => s.Trim().Length <= SenhaMaxLength)
            .WithMessage($"A senha tem no máximo {SenhaMaxLength} caracteres.");
        RuleFor(x => x.Data).NotEqual(default(DateOnly));
        RuleFor(x => x.FilaId).NotEmpty();
    }
}
