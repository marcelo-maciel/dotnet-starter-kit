using FluentValidation;
using FSH.Modules.PAF.Domain;

namespace FSH.Modules.PAF.Features.v1.Filas;

internal static class FilaValidation
{
    public static IRuleBuilderOptions<T, string> LetraDaSenha<T>(this IRuleBuilder<T, string> regra) =>
        regra.Must(l => !string.IsNullOrWhiteSpace(l) && l.Trim().Length <= FilaPrioridade.LetraMaxLength)
            .WithMessage($"A letra da senha tem de 1 a {FilaPrioridade.LetraMaxLength} caracteres.");

    // RF02: both hours or none ("sem restrição"), and the final one after the initial one.
    public static bool JanelaValida(TimeOnly? horaInicial, TimeOnly? horaFinal) =>
        horaInicial.HasValue == horaFinal.HasValue && !(horaInicial >= horaFinal);

    public const string MensagemJanela =
        "Informe as duas horas, com a final depois da inicial, ou nenhuma para fila sem restrição.";
}
