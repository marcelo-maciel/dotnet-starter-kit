using FluentValidation;
using FSH.Modules.PAF.Contracts.v1.UsuariosUnidade;

namespace FSH.Modules.PAF.Features.v1.UsuariosUnidade.SetUsuarioUnidades;

public sealed class SetUsuarioUnidadesCommandValidator : AbstractValidator<SetUsuarioUnidadesCommand>
{
    public SetUsuarioUnidadesCommandValidator()
    {
        RuleFor(x => x.UsuarioId).NotEmpty();

        // Every user has a unidade principal, the first of the list (ADR 0006).
        RuleFor(x => x.Unidades).NotEmpty().WithMessage("O usuário precisa de ao menos uma unidade, a principal.");
        RuleForEach(x => x.Unidades).NotEmpty();
        RuleFor(x => x.Unidades)
            .Must(us => us.Distinct().Count() == us.Count)
            .When(x => x.Unidades is not null)
            .WithMessage("A mesma unidade aparece duas vezes.");
    }
}
