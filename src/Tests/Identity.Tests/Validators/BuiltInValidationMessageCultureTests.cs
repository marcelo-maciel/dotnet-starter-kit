using FSH.Modules.Identity.Contracts.v1.Tokens.TokenGeneration;
using FSH.Modules.Identity.Contracts.v1.Users.ForgotPassword;
using FSH.Modules.Identity.Features.v1.Tokens.TokenGeneration;
using FSH.Modules.Identity.Features.v1.Users.ForgotPassword;
using Identity.Tests.Support;

namespace Identity.Tests.Validators;

/// <summary>
/// The login and forgot-password validators rely on FluentValidation's built-in messages (no WithMessage).
/// FluentValidation ships its own catalogs keyed off CurrentUICulture, so these pin what a pt-BR client
/// actually reads, property display name included, next to the English the default culture gives.
/// </summary>
public sealed class BuiltInValidationMessageCultureTests
{
    [Theory]
    [InlineData("en-US", "'Email' must not be empty.")]
    [InlineData("pt-BR", "'Email' deve ser informado.")]
    public void Login_with_empty_email_reports_in_the_request_culture(string culture, string expected)
    {
        using var scope = new UiCultureScope(culture);

        var result = new GenerateTokenCommandValidator().Validate(new GenerateTokenCommand(string.Empty, "Senha123!"));

        result.Errors.Single().ErrorMessage.ShouldBe(expected);
    }

    [Theory]
    [InlineData("en-US", "'Email' is not a valid email address.")]
    [InlineData("pt-BR", "'Email' é um endereço de email inválido.")]
    public void Forgot_password_with_malformed_email_reports_in_the_request_culture(string culture, string expected)
    {
        using var scope = new UiCultureScope(culture);

        var result = new ForgotPasswordCommandValidator().Validate(new ForgotPasswordCommand { Email = "sem-arroba" });

        result.Errors.Single().ErrorMessage.ShouldBe(expected);
    }
}
