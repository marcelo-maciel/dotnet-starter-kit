using FluentValidation;
using FSH.Modules.PAF.Contracts.v1.Filas;

namespace FSH.Modules.PAF.Features.v1.Filas.CreateFila;

public sealed class CreateFilaCommandValidator : AbstractValidator<CreateFilaCommand>
{
    public CreateFilaCommandValidator()
    {
        RuleFor(x => x.Nome).NotEmpty().MaximumLength(120);
        RuleFor(x => x.NomeExibicao).NotEmpty().MaximumLength(120);
        RuleFor(x => x).Must(x => FilaValidation.JanelaValida(x.HoraInicial, x.HoraFinal))
            .WithName("HoraFinal")
            .WithMessage(FilaValidation.MensagemJanela);
        RuleFor(x => x.SetorId).Must(id => id != Guid.Empty).When(x => x.SetorId.HasValue);
        RuleFor(x => x.Aparencia).NotNull().SetValidator(new AparenciaFilaValidator());

        // A fila with no prioridade never offers a button nor finds a candidate (RF04, "estação muda").
        RuleFor(x => x.Prioridades).NotEmpty().WithMessage("A fila precisa de ao menos uma prioridade.");
        RuleForEach(x => x.Prioridades).ChildRules(p =>
        {
            p.RuleFor(i => i.PrioridadeId).NotEmpty();
            p.RuleFor(i => i.Letra).LetraDaSenha();
            p.RuleFor(i => i.NumeroInicial).GreaterThanOrEqualTo(0);
            p.RuleFor(i => i.CorBotao).CorOpcional();
        });
        RuleFor(x => x.Prioridades)
            .Must(ps => ps.Select(p => p.PrioridadeId).Distinct().Count() == ps.Count)
            .When(x => x.Prioridades is not null)
            .WithMessage("A mesma prioridade aparece duas vezes na grade.");
        RuleFor(x => x.Prioridades)
            .Must(ps => ps.Select(p => (p.Letra ?? string.Empty).Trim().ToUpperInvariant()).Distinct().Count() == ps.Count)
            .When(x => x.Prioridades is not null)
            .WithMessage("A mesma letra de senha aparece duas vezes na grade.");
    }
}
