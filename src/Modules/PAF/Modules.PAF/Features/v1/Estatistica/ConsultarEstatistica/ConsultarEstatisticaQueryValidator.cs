using FluentValidation;
using FSH.Modules.PAF.Contracts.v1.Estatistica;

namespace FSH.Modules.PAF.Features.v1.Estatistica.ConsultarEstatistica;

/// <summary>
/// Shape only (400). Scope and permission are decided by the handler (403), because they need the caller and
/// the database. A filter a type does not use is refused instead of ignored: the result would silently answer
/// a different question.
/// </summary>
public sealed class ConsultarEstatisticaQueryValidator : AbstractValidator<ConsultarEstatisticaQuery>
{
    private static readonly TipoEstatistica[] TiposComAgrupamento =
        [TipoEstatistica.TotaisPorTipoDeAtendimento, TipoEstatistica.TotalDeAtendimentos];

    public ConsultarEstatisticaQueryValidator()
    {
        RuleFor(x => x.Tipo).NotNull().IsInEnum().NotEqual(TipoEstatistica.Nenhum);
        RuleFor(x => x.DataInicial).NotNull();
        RuleFor(x => x.DataFinal).NotNull();
        RuleFor(x => x.DataFinal)
            .GreaterThanOrEqualTo(x => x.DataInicial)
            .When(x => x.DataInicial is not null && x.DataFinal is not null)
            .WithMessage("A data final não pode ser anterior à data inicial.");

        RuleFor(x => x.Agrupamento)
            .NotNull().WithMessage("Os tipos 9 e 10 exigem o agrupamento por fila ou por setor.")
            .IsInEnum()
            .When(x => x.Tipo is { } tipo && TiposComAgrupamento.Contains(tipo));
        RuleFor(x => x.Agrupamento).Null()
            .When(x => x.Tipo is not { } tipo || !TiposComAgrupamento.Contains(tipo))
            .WithMessage("Só os tipos 9 e 10 aceitam agrupamento.");

        RuleFor(x => x.SomenteEstacoesComAtendimento).Equal(false)
            .When(x => x.Tipo != TipoEstatistica.AtendimentosPorGuiche)
            .WithMessage("Só o tipo 3 aceita filtrar as estações com atendimento.");

        RuleForEach(x => x.Unidades).NotEmpty();
        RuleForEach(x => x.Filas).NotEmpty();
        RuleForEach(x => x.Prioridades).NotEmpty();
    }
}
