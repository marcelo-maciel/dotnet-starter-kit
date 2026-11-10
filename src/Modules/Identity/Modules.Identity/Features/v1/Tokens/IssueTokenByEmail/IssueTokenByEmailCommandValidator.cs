using FluentValidation;
using FSH.Modules.Identity.Contracts.v1.Tokens.IssueTokenByEmail;

namespace FSH.Modules.Identity.Features.v1.Tokens.IssueTokenByEmail;

public sealed class IssueTokenByEmailCommandValidator : AbstractValidator<IssueTokenByEmailCommand>
{
    public IssueTokenByEmailCommandValidator()
    {
        RuleFor(p => p.Email)
            .Cascade(CascadeMode.Stop)
            .NotEmpty()
            .EmailAddress();

        RuleFor(p => p.Password)
            .Cascade(CascadeMode.Stop)
            .NotEmpty();
    }
}
