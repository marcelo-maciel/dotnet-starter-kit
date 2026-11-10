using FluentValidation;
using FSH.Modules.PAF.Contracts.v1.Estacoes;

namespace FSH.Modules.PAF.Features.v1.Estacoes.EnableEstacao;

public sealed class EnableEstacaoCommandValidator : AbstractValidator<EnableEstacaoCommand>
{
    public EnableEstacaoCommandValidator()
    {
        RuleFor(x => x.Id).NotEmpty();
    }
}
