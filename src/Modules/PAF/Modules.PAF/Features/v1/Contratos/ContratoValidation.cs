using FluentValidation;
using FSH.Modules.PAF.Domain;

namespace FSH.Modules.PAF.Features.v1.Contratos;

internal static class ContratoValidation
{
    public static IRuleBuilderOptions<T, string> Politica<T>(this IRuleBuilder<T, string> regra) =>
        regra.Must(p => Enum.TryParse<PoliticaLimiteLicenca>(p, ignoreCase: false, out _))
            .WithMessage("Política deve ser Recusar ou UltrapassarComAviso.");

    public static bool VigenciaValida(DateOnly inicio, DateOnly? fim) => fim is null || fim >= inicio;

    public const string MensagemVigencia = "O fim da vigência não pode ser anterior ao início.";
}
