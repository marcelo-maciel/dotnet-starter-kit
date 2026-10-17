using FluentValidation;
using FSH.Modules.PAF.Contracts.v1.Filas;

namespace FSH.Modules.PAF.Features.v1.Filas.UpdateFilaPrioridade;

public sealed class UpdateFilaPrioridadeCommandValidator : AbstractValidator<UpdateFilaPrioridadeCommand>
{
    public UpdateFilaPrioridadeCommandValidator()
    {
        RuleFor(x => x.FilaId).NotEmpty();
        RuleFor(x => x.PrioridadeId).NotEmpty();
        RuleFor(x => x.Letra).LetraDaSenha();
        RuleFor(x => x.NumeroInicial).GreaterThanOrEqualTo(0);
    }
}
