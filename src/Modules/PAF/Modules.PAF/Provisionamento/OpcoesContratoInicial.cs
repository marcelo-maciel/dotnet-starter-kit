using FSH.Modules.PAF.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace FSH.Modules.PAF.Provisionamento;

/// <summary>
/// Contrato com que a unidade inicial nasce na criação do tenant (RF23, decidido em 21/10/2026): sem ele, nenhum login
/// do cliente passa até a Codefi lembrar de gravar um. O operador ajusta depois em "Unidades e contratos" do admin.
/// Valores em <c>PAF:ContratoInicial</c>, validados na inicialização.
/// </summary>
public sealed class OpcoesContratoInicial
{
    public const string Secao = "PAF:ContratoInicial";

    /// <summary>Same ceiling as the operator's contract form (<c>CreateContratoUnidadeCommandValidator</c>).</summary>
    public const int TamanhoMaximoPlano = 120;

    public string Plano { get; set; } = "Inicial";

    public int LimiteSessoes { get; set; } = 5;

    // Recusar: a client forgotten on the default contract cannot grow past it unnoticed.
    public PoliticaLimiteLicenca Politica { get; set; } = PoliticaLimiteLicenca.Recusar;

    public static void Registrar(IServiceCollection services)
    {
        services.AddOptions<OpcoesContratoInicial>()
            .BindConfiguration(Secao)
            .Validate(o => !string.IsNullOrWhiteSpace(o.Plano) && o.Plano.Length <= TamanhoMaximoPlano,
                "PAF:ContratoInicial:Plano precisa ter texto e no máximo 120 caracteres.")
            .Validate(o => o.LimiteSessoes >= 1, "PAF:ContratoInicial:LimiteSessoes precisa ser pelo menos 1.")
            .Validate(o => Enum.IsDefined(o.Politica), "PAF:ContratoInicial:Politica precisa ser Recusar ou UltrapassarComAviso.")
            .ValidateOnStart();
    }
}
