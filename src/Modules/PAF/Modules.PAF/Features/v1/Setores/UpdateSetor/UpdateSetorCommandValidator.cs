using FluentValidation;
using FSH.Modules.PAF.Contracts.v1.Setores;

namespace FSH.Modules.PAF.Features.v1.Setores.UpdateSetor;

public sealed class UpdateSetorCommandValidator : AbstractValidator<UpdateSetorCommand>
{
    public UpdateSetorCommandValidator()
    {
        RuleFor(x => x.Id).NotEmpty();
        RuleFor(x => x.Nome).Must(n => !string.IsNullOrWhiteSpace(n)).MaximumLength(NomeDoSetor.MaxLength);
    }
}
