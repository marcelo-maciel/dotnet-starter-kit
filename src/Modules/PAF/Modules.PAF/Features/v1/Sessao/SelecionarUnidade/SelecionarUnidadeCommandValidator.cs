using FluentValidation;
using FSH.Modules.PAF.Contracts.v1.Sessao;

namespace FSH.Modules.PAF.Features.v1.Sessao.SelecionarUnidade;

public sealed class SelecionarUnidadeCommandValidator : AbstractValidator<SelecionarUnidadeCommand>
{
    public SelecionarUnidadeCommandValidator()
    {
        RuleFor(x => x.UnidadeId).NotEmpty();
    }
}
