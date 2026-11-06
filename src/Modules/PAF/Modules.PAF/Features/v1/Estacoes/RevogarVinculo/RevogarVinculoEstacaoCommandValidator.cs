using FluentValidation;
using FSH.Modules.PAF.Contracts.v1.Estacoes;

namespace FSH.Modules.PAF.Features.v1.Estacoes.RevogarVinculo;

public sealed class RevogarVinculoEstacaoCommandValidator : AbstractValidator<RevogarVinculoEstacaoCommand>
{
    public RevogarVinculoEstacaoCommandValidator()
    {
        RuleFor(x => x.EstacaoId).NotEmpty();
    }
}
