using FluentValidation;
using FSH.Modules.PAF.Contracts.v1.Estacoes;

namespace FSH.Modules.PAF.Features.v1.Estacoes.MidiasDaEstacao;

// Shape only; station role and mídia state are checked under the lock by the handler.
public sealed class SubstituirMidiasDaEstacaoCommandValidator : AbstractValidator<SubstituirMidiasDaEstacaoCommand>
{
    public SubstituirMidiasDaEstacaoCommandValidator()
    {
        RuleFor(x => x.EstacaoId).NotEmpty();
        RuleFor(x => x.Midias).NotNull();
        RuleFor(x => x.Midias.Count)
            .LessThanOrEqualTo(SubstituirMidiasDaEstacaoCommand.MaximoItens)
            .When(x => x.Midias is not null)
            .WithMessage($"A lista de exibição aceita até {SubstituirMidiasDaEstacaoCommand.MaximoItens} itens.");
        RuleForEach(x => x.Midias).NotEmpty();
    }
}
