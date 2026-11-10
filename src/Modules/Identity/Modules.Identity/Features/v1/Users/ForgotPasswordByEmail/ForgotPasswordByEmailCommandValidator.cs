using FluentValidation;
using FSH.Modules.Identity.Contracts.v1.Users.ForgotPasswordByEmail;

namespace FSH.Modules.Identity.Features.v1.Users.ForgotPasswordByEmail;

public sealed class ForgotPasswordByEmailCommandValidator : AbstractValidator<ForgotPasswordByEmailCommand>
{
    public ForgotPasswordByEmailCommandValidator()
    {
        RuleFor(p => p.Email)
            .Cascade(CascadeMode.Stop)
            .NotEmpty()
            .EmailAddress();
    }
}
