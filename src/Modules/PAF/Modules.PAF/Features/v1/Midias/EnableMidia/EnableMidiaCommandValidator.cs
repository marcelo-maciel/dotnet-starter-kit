using FluentValidation;
using FSH.Modules.PAF.Contracts.v1.Midias;

namespace FSH.Modules.PAF.Features.v1.Midias.EnableMidia;

public sealed class EnableMidiaCommandValidator : AbstractValidator<EnableMidiaCommand>
{
    public EnableMidiaCommandValidator() => RuleFor(x => x.Id).NotEmpty();
}
