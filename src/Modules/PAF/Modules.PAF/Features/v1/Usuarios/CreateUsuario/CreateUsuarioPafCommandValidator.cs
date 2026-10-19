using FluentValidation;
using FSH.Modules.PAF.Contracts.v1.Usuarios;
using FSH.Modules.PAF.Domain;
using FSH.Modules.PAF.Features.v1.UsuariosUnidade.SetUsuarioUnidades;

namespace FSH.Modules.PAF.Features.v1.Usuarios.CreateUsuario;

public sealed class CreateUsuarioPafCommandValidator : AbstractValidator<CreateUsuarioPafCommand>
{
    public const int NomeMaxLength = 100;
    public const string MensagemSenhasDiferentes = "A senha e a confirmação não conferem.";
    public const string MensagemPapelInvalido = "Papel deve ser Administrador, Gerente ou Atendente.";

    public CreateUsuarioPafCommandValidator()
    {
        RuleFor(x => x.Nome).NotEmpty().MaximumLength(NomeMaxLength);
        RuleFor(x => x.Sobrenome).NotEmpty().MaximumLength(NomeMaxLength);
        // The login is the e-mail (D14); the kit's user name is the same value.
        RuleFor(x => x.Email).NotEmpty().EmailAddress().MaximumLength(256);
        RuleFor(x => x.Senha).NotEmpty();
        RuleFor(x => x.ConfirmacaoSenha).Equal(x => x.Senha).WithMessage(MensagemSenhasDiferentes);
        RuleFor(x => x.Funcao).MaximumLength(PerfilUsuario.FuncaoMaxLength);
        RuleFor(x => x.Ramal).MaximumLength(PerfilUsuario.RamalMaxLength);
        RuleFor(x => x.Papel).Must(p => PapeisDoUsuarioPaf.Nomes.Contains(p)).WithMessage(MensagemPapelInvalido);

        // Same shape as the scope edit, checked here so no kit user is created for a list the edit would refuse.
        RuleFor(x => x.Unidades).NotEmpty().WithMessage("O usuário precisa de ao menos uma unidade, a principal.");
        RuleForEach(x => x.Unidades).NotEmpty();
        RuleFor(x => x.Unidades)
            .Must(us => us.Distinct().Count() == us.Count)
            .When(x => x.Unidades is not null)
            .WithMessage("A mesma unidade aparece duas vezes.");
        RuleFor(x => x.Unidades)
            .Must(us => us.Count == 1)
            .When(x => x.EscopoEmpresa && x.Unidades is not null)
            .WithMessage(SetUsuarioUnidadesCommandValidator.MensagemEmpresaUmaUnidade);
    }
}
