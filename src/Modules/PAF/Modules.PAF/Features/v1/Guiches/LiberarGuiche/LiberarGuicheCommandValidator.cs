using FluentValidation;
using FSH.Modules.PAF.Contracts.v1.Guiches;

namespace FSH.Modules.PAF.Features.v1.Guiches.LiberarGuiche;

public sealed class LiberarGuicheCommandValidator : AbstractValidator<LiberarGuicheCommand>
{
    public LiberarGuicheCommandValidator()
    {
        RuleFor(x => x.EstacaoId).NotEmpty();
    }
}
