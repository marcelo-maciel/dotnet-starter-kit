using FluentValidation;
using FSH.Modules.PAF.Contracts.v1.Estacoes;

namespace FSH.Modules.PAF.Features.v1.Estacoes.GerarVinculo;

public sealed class GerarVinculoEstacaoCommandValidator : AbstractValidator<GerarVinculoEstacaoCommand>
{
    public GerarVinculoEstacaoCommandValidator()
    {
        RuleFor(x => x.EstacaoId).NotEmpty();
    }
}
