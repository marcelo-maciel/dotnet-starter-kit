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
