using FluentValidation;
using FSH.Modules.PAF.Contracts.v1.Filas;

namespace FSH.Modules.PAF.Features.v1.Filas.AddFilaPrioridade;

public sealed class AddFilaPrioridadeCommandValidator : AbstractValidator<AddFilaPrioridadeCommand>
{
    public AddFilaPrioridadeCommandValidator()
    {
        RuleFor(x => x.FilaId).NotEmpty();
        RuleFor(x => x.PrioridadeId).NotEmpty();
        RuleFor(x => x.Letra).LetraDaSenha();
        RuleFor(x => x.NumeroInicial).GreaterThanOrEqualTo(0);
        RuleFor(x => x.CorBotao).CorOpcional();
    }
}
