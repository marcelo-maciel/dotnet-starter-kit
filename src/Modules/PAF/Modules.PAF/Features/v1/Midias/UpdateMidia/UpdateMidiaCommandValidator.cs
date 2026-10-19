using FluentValidation;
using FSH.Modules.PAF.Contracts.v1.Midias;

namespace FSH.Modules.PAF.Features.v1.Midias.UpdateMidia;

public sealed class UpdateMidiaCommandValidator : AbstractValidator<UpdateMidiaCommand>
{
    public UpdateMidiaCommandValidator()
    {
        RuleFor(x => x.Id).NotEmpty();
        RuleFor(x => x.Versao).GreaterThanOrEqualTo(0);
        RuleFor(x => x.Descricao).DescricaoDaMidia();
        RuleFor(x => x.DuracaoSegundos).DuracaoDaMidia();
    }
}
