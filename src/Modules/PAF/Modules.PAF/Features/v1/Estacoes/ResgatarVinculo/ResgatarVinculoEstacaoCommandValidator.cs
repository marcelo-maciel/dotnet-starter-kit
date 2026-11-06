using FluentValidation;
using FSH.Modules.PAF.Contracts.v1.Estacoes;

namespace FSH.Modules.PAF.Features.v1.Estacoes.ResgatarVinculo;

public sealed class ResgatarVinculoEstacaoCommandValidator : AbstractValidator<ResgatarVinculoEstacaoCommand>
{
    public ResgatarVinculoEstacaoCommandValidator()
    {
        // 32 random bytes in base64url are 43 chars; the cap only stops oversized input before hashing.
        RuleFor(x => x.Token).NotEmpty().MaximumLength(128);
    }
}
