using FluentValidation;
using FSH.Modules.PAF.Contracts.v1.Midias;
using FSH.Modules.PAF.Domain;
using FSH.Modules.PAF.Midias;

namespace FSH.Modules.PAF.Features.v1.ProgramacoesMidia;

/// <summary>Request rules of the RF10 form, shared by create and edit.</summary>
internal static class RegrasProgramacao
{
    public static IRuleBuilderOptions<T, string> NomeDaProgramacao<T>(this IRuleBuilder<T, string> regra) =>
        regra.Must(n => !string.IsNullOrWhiteSpace(n)).WithMessage("O nome é obrigatório.")
            .Must(n => n is null || n.Trim().Length <= ProgramacaoMidia.NomeMaxLength)
            .WithMessage($"O nome tem no máximo {ProgramacaoMidia.NomeMaxLength} caracteres.");

    public static IRuleBuilderOptions<T, int> DiasDaSemana<T>(this IRuleBuilder<T, int> regra) =>
        regra.InclusiveBetween((int)DiasSemana.Domingo, (int)DiasSemana.Todos)
            .WithMessage("Escolha ao menos um dia da semana.");

    // One repetition of a one-second mídia per second of the day is the most that fits before midnight (D13).
    public static IRuleBuilderOptions<T, int> RepeticoesDaProgramacao<T>(this IRuleBuilder<T, int> regra) =>
        regra.InclusiveBetween(1, (int)AgendaMidia.SegundosNoDia)
            .WithMessage($"As repetições vão de 1 a {AgendaMidia.SegundosNoDia}.");

    // Whole seconds: the window is computed in seconds, and a fraction would make the start and the search disagree.
    public static IRuleBuilderOptions<T, TimeOnly> HoraEmSegundosInteiros<T>(this IRuleBuilder<T, TimeOnly> regra) =>
        regra.Must(h => h.Ticks % TimeSpan.TicksPerSecond == 0).WithMessage("A hora inicial não aceita fração de segundo.");
}

public sealed class CreateProgramacaoMidiaCommandValidator : AbstractValidator<CreateProgramacaoMidiaCommand>
{
    public CreateProgramacaoMidiaCommandValidator()
    {
        RuleFor(x => x.MidiaId).NotEmpty();
        RuleFor(x => x.Nome).NomeDaProgramacao();
        RuleFor(x => x.DataFinal).GreaterThanOrEqualTo(x => x.DataInicial).WithMessage("A data final não pode ser anterior à inicial.");
        RuleFor(x => x.HoraInicial).HoraEmSegundosInteiros();
        RuleFor(x => x.Repeticoes).RepeticoesDaProgramacao();
        RuleFor(x => x.Dias).DiasDaSemana();
    }
}

public sealed class UpdateProgramacaoMidiaCommandValidator : AbstractValidator<UpdateProgramacaoMidiaCommand>
{
    public UpdateProgramacaoMidiaCommandValidator()
    {
        RuleFor(x => x.Id).NotEmpty();
        RuleFor(x => x.Versao).GreaterThanOrEqualTo(0);
        RuleFor(x => x.MidiaId).NotEmpty();
        RuleFor(x => x.Nome).NomeDaProgramacao();
        RuleFor(x => x.DataFinal).GreaterThanOrEqualTo(x => x.DataInicial).WithMessage("A data final não pode ser anterior à inicial.");
        RuleFor(x => x.HoraInicial).HoraEmSegundosInteiros();
        RuleFor(x => x.Repeticoes).RepeticoesDaProgramacao();
        RuleFor(x => x.Dias).DiasDaSemana();
    }
}

public sealed class EnableProgramacaoMidiaCommandValidator : AbstractValidator<EnableProgramacaoMidiaCommand>
{
    public EnableProgramacaoMidiaCommandValidator() => RuleFor(x => x.Id).NotEmpty();
}

public sealed class DisableProgramacaoMidiaCommandValidator : AbstractValidator<DisableProgramacaoMidiaCommand>
{
    public DisableProgramacaoMidiaCommandValidator() => RuleFor(x => x.Id).NotEmpty();
}

public sealed class DeleteProgramacaoMidiaCommandValidator : AbstractValidator<DeleteProgramacaoMidiaCommand>
{
    public DeleteProgramacaoMidiaCommandValidator() => RuleFor(x => x.Id).NotEmpty();
}

public sealed class SearchProgramacoesMidiaQueryValidator : AbstractValidator<SearchProgramacoesMidiaQuery>
{
    public SearchProgramacoesMidiaQueryValidator()
    {
        RuleFor(x => x.Nome).MaximumLength(ProgramacaoMidia.NomeMaxLength);
        RuleFor(x => x.Dias!.Value).DiasDaSemana().When(x => x.Dias.HasValue);
        RuleFor(x => x.Ate).GreaterThanOrEqualTo(x => x.De).When(x => x.De.HasValue && x.Ate.HasValue)
            .WithMessage("O fim do período não pode ser anterior ao início.");
    }
}
