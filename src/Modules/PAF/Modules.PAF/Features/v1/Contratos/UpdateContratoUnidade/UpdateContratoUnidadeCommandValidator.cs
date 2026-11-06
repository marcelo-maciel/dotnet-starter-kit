using FluentValidation;
using FSH.Modules.PAF.Contracts.v1.Contratos;

namespace FSH.Modules.PAF.Features.v1.Contratos.UpdateContratoUnidade;

public sealed class UpdateContratoUnidadeCommandValidator : AbstractValidator<UpdateContratoUnidadeCommand>
{
    public UpdateContratoUnidadeCommandValidator()
    {
        RuleFor(x => x.Id).NotEmpty();
        RuleFor(x => x.Plano).NotEmpty().MaximumLength(120);
        RuleFor(x => x.LimiteSessoes).GreaterThanOrEqualTo(0);
        RuleFor(x => x.Politica).Politica();
        RuleFor(x => x).Must(x => ContratoValidation.VigenciaValida(x.InicioVigencia, x.FimVigencia))
            .WithName("FimVigencia")
            .WithMessage(ContratoValidation.MensagemVigencia);
    }
}
