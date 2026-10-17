using FluentValidation;
using FSH.Modules.PAF.Contracts.v1.Sessao;

namespace FSH.Modules.PAF.Features.v1.Sessao.SetUnidadeCorrente;

public sealed class SetUnidadeCorrenteCommandValidator : AbstractValidator<SetUnidadeCorrenteCommand>
{
    public SetUnidadeCorrenteCommandValidator()
    {
        RuleFor(x => x.UnidadeId).NotEmpty();
    }
}
