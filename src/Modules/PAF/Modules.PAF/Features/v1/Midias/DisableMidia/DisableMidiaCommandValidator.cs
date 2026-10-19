using FluentValidation;
using FSH.Modules.PAF.Contracts.v1.Midias;

namespace FSH.Modules.PAF.Features.v1.Midias.DisableMidia;

public sealed class DisableMidiaCommandValidator : AbstractValidator<DisableMidiaCommand>
{
    public DisableMidiaCommandValidator() => RuleFor(x => x.Id).NotEmpty();
}
