using FluentValidation;
using FSH.Modules.PAF.Contracts.v1.Sessao;

namespace FSH.Modules.PAF.Features.v1.Sessao.SetGuiche;

public sealed class SetGuicheCommandValidator : AbstractValidator<SetGuicheCommand>
{
    public SetGuicheCommandValidator()
    {
        RuleFor(x => x.EstacaoId).NotEmpty();
    }
}
