using FluentValidation;
using FSH.Modules.PAF.Contracts.v1.Usuarios;
using FSH.Modules.PAF.Domain;
using FSH.Modules.PAF.Features.v1.Usuarios.CreateUsuario;

namespace FSH.Modules.PAF.Features.v1.Usuarios.UpdateUsuario;

public sealed class UpdateUsuarioPafCommandValidator : AbstractValidator<UpdateUsuarioPafCommand>
{
    public UpdateUsuarioPafCommandValidator()
    {
        RuleFor(x => x.UsuarioId).NotEmpty();
        RuleFor(x => x.Nome).NotEmpty().MaximumLength(CreateUsuarioPafCommandValidator.NomeMaxLength);
        RuleFor(x => x.Sobrenome).NotEmpty().MaximumLength(CreateUsuarioPafCommandValidator.NomeMaxLength);
        RuleFor(x => x.Funcao).MaximumLength(PerfilUsuario.FuncaoMaxLength);
        RuleFor(x => x.Ramal).MaximumLength(PerfilUsuario.RamalMaxLength);
    }
}
