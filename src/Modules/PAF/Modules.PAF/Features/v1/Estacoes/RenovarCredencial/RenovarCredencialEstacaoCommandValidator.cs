using FluentValidation;
using FSH.Modules.PAF.Contracts.v1.Estacoes;

namespace FSH.Modules.PAF.Features.v1.Estacoes.RenovarCredencial;

public sealed class RenovarCredencialEstacaoCommandValidator : AbstractValidator<RenovarCredencialEstacaoCommand>
{
    public RenovarCredencialEstacaoCommandValidator()
    {
        RuleFor(x => x.Segredo).NotEmpty().MaximumLength(128);
    }
}
