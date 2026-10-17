using FluentValidation;
using FSH.Modules.PAF.Contracts.v1.Sessao;

namespace FSH.Modules.PAF.Features.v1.Sessao.SelecionarGuiche;

public sealed class SelecionarGuicheCommandValidator : AbstractValidator<SelecionarGuicheCommand>
{
    public SelecionarGuicheCommandValidator()
    {
        RuleFor(x => x.EstacaoId).NotEmpty();
    }
}
