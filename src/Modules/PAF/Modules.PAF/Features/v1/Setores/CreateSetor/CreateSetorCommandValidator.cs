using FluentValidation;
using FSH.Modules.PAF.Contracts.v1.Setores;

namespace FSH.Modules.PAF.Features.v1.Setores.CreateSetor;

public sealed class CreateSetorCommandValidator : AbstractValidator<CreateSetorCommand>
{
    public CreateSetorCommandValidator()
    {
        RuleFor(x => x.Nome).Must(n => !string.IsNullOrWhiteSpace(n)).MaximumLength(NomeDoSetor.MaxLength);
    }
}
