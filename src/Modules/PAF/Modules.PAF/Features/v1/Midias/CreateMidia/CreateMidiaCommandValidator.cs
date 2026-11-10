using FluentValidation;
using FSH.Modules.PAF.Contracts.v1.Midias;

namespace FSH.Modules.PAF.Features.v1.Midias.CreateMidia;

public sealed class CreateMidiaCommandValidator : AbstractValidator<CreateMidiaCommand>
{
    public CreateMidiaCommandValidator()
    {
        RuleFor(x => x.EnvioId).NotEmpty();
        RuleFor(x => x.Descricao).DescricaoDaMidia();
        RuleFor(x => x.DuracaoSegundos).DuracaoDaMidia();
    }
}
