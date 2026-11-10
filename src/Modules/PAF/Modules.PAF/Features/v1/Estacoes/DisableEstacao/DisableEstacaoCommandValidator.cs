using FluentValidation;
using FSH.Modules.PAF.Contracts.v1.Estacoes;

namespace FSH.Modules.PAF.Features.v1.Estacoes.DisableEstacao;

public sealed class DisableEstacaoCommandValidator : AbstractValidator<DisableEstacaoCommand>
{
    public DisableEstacaoCommandValidator()
    {
        RuleFor(x => x.Id).NotEmpty();
    }
}
