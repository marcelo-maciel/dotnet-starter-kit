using FluentValidation;
using FSH.Modules.PAF.Contracts.v1.Usuarios;

namespace FSH.Modules.PAF.Features.v1.Usuarios;

public sealed class DesativarUsuarioPafCommandValidator : AbstractValidator<DesativarUsuarioPafCommand>
{
    public DesativarUsuarioPafCommandValidator() => RuleFor(x => x.UsuarioId).NotEmpty();
}

public sealed class CopiarPerfilUsuarioPafCommandValidator : AbstractValidator<CopiarPerfilUsuarioPafCommand>
{
    public CopiarPerfilUsuarioPafCommandValidator()
    {
        RuleFor(x => x.UsuarioId).NotEmpty();
        RuleFor(x => x.UsuarioOrigemId).NotEmpty().NotEqual(x => x.UsuarioId).WithMessage("Escolha outro usuário como origem.");
    }
}

public sealed class DefinirSenhaUsuarioPafCommandValidator : AbstractValidator<DefinirSenhaUsuarioPafCommand>
{
    public DefinirSenhaUsuarioPafCommandValidator()
    {
        RuleFor(x => x.UsuarioId).NotEmpty();
        // Explicit messages: the default ones of NotEmpty/Equal can interpolate the compared value, here the password.
        RuleFor(x => x.Senha).NotEmpty().WithMessage("Informe a nova senha.");
        RuleFor(x => x.ConfirmacaoSenha).Equal(x => x.Senha).WithMessage(CreateUsuario.CreateUsuarioPafCommandValidator.MensagemSenhasDiferentes);
    }
}
