using FluentValidation;
using FSH.Modules.PAF.Contracts.v1.Prioridades;

namespace FSH.Modules.PAF.Features.v1.Prioridades.EnablePrioridade;

public sealed class EnablePrioridadeCommandValidator : AbstractValidator<EnablePrioridadeCommand>
{
    public EnablePrioridadeCommandValidator()
    {
        RuleFor(x => x.Id).NotEmpty();
    }
}
