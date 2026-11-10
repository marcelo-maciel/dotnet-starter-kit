using FluentValidation;
using FSH.Modules.PAF.Contracts.v1.Estacoes;
using FSH.Modules.PAF.Domain;

namespace FSH.Modules.PAF.Features.v1.Estacoes.UpdateEstacao;

// Shape only; the rules that depend on the stored station type run in ListasDaEstacao.
public sealed class UpdateEstacaoCommandValidator : AbstractValidator<UpdateEstacaoCommand>
{
    public UpdateEstacaoCommandValidator()
    {
        RuleFor(x => x.Id).NotEmpty();
        RuleFor(x => x.Nome).NotEmpty().MaximumLength(120);
        RuleFor(x => x.Ramal).MaximumLength(Estacao.RamalMaxLength);
        RuleFor(x => x.Configuracao).NotNull();

        RuleFor(x => x.Filas).NotNull();
        RuleForEach(x => x.Filas).ChildRules(f =>
        {
            f.RuleFor(i => i.FilaId).NotEmpty();
            f.RuleFor(i => i.Papel).Must(p => Enum.TryParse<PapelEstacao>(p, ignoreCase: false, out _))
                .WithMessage("Papel deve ser Emissao, Guiche ou Painel.");
        });
        RuleFor(x => x.Filas)
            .Must(fs => fs.Select(f => (f.Papel, f.FilaId)).Distinct().Count() == fs.Count)
            .When(x => x.Filas is not null)
            .WithMessage("A mesma fila aparece duas vezes no mesmo papel.");

        RuleFor(x => x.Prioridades).NotNull();
        RuleForEach(x => x.Prioridades).ChildRules(p => p.RuleFor(i => i.PrioridadeId).NotEmpty());
        RuleFor(x => x.Prioridades)
            .Must(ps => ps.Select(p => p.PrioridadeId).Distinct().Count() == ps.Count)
            .When(x => x.Prioridades is not null)
            .WithMessage("A mesma prioridade aparece duas vezes.");
    }
}
