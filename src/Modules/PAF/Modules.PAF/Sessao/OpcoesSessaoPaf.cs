using Microsoft.Extensions.DependencyInjection;

namespace FSH.Modules.PAF.Sessao;

/// <summary>
/// Vida da sessão pela conexão de tempo real (RF22, seção 9; requisitos 8.1, 03/10/2026): valores iniciais
/// configuráveis em <c>PAF:Sessao</c>, a confirmar com medição. Validados na inicialização: um valor fora da faixa
/// derruba o host em vez de soltar licenças em massa ou nunca soltá-las.
/// </summary>
public sealed class OpcoesSessaoPaf
{
    public const string Secao = "PAF:Sessao";

    /// <summary>Tempo que a sessão sem nenhuma conexão viva mantém licença e guichê, esperando a reconexão.</summary>
    public TimeSpan CarenciaReconexao { get; set; } = TimeSpan.FromMinutes(2);

    // ponytail: the licence count, the session check middleware and the session state still read the constant of
    // Licencas with the same initial value; binding them to this option is a one-line change in Licencas (S09 file).
    /// <summary>Prazo da marca de sessão viva em <c>AtividadeSessao</c>.</summary>
    public TimeSpan PrazoMarca { get; set; } = TimeSpan.FromMinutes(Licencas.PrazoMarcaMinutos);

    /// <summary>Intervalo de renovação das entradas do registro de conexões e da marca pela conexão.</summary>
    public TimeSpan IntervaloRenovacao { get; set; } = TimeSpan.FromMinutes(Licencas.IntervaloMarcaMinutos);

    /// <summary>
    /// Validade de uma entrada do registro sem renovação: duas renovações perdidas. A entrada da instância que caiu
    /// sem rodar a desconexão some depois disso.
    /// </summary>
    public TimeSpan ValidadeEntrada => IntervaloRenovacao * 2;

    /// <summary>
    /// Passo do laço de renovação: a soltura pela carência não pode esperar um intervalo inteiro, senão uma carência
    /// de 30 s soltaria em até 90 s.
    /// </summary>
    public TimeSpan Passo => (CarenciaReconexao < IntervaloRenovacao ? CarenciaReconexao : IntervaloRenovacao) / 2;

    public static void Registrar(IServiceCollection services)
    {
        services.AddOptions<OpcoesSessaoPaf>()
            .BindConfiguration(Secao)
            .Validate(o => o.CarenciaReconexao > TimeSpan.Zero, "PAF:Sessao:CarenciaReconexao precisa ser positiva.")
            .Validate(o => o.CarenciaReconexao <= o.PrazoMarca, "PAF:Sessao:CarenciaReconexao não pode passar do PrazoMarca.")
            .Validate(o => o.IntervaloRenovacao > TimeSpan.Zero, "PAF:Sessao:IntervaloRenovacao precisa ser positivo.")
            .Validate(o => o.IntervaloRenovacao < o.PrazoMarca, "PAF:Sessao:IntervaloRenovacao precisa ser menor que o PrazoMarca.")
            .ValidateOnStart();
    }
}
