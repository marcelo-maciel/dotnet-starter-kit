using FSH.Modules.PAF.Sessao;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace PAF.Tests;

/// <summary>Options <c>PAF:Sessao</c>: initial values, binding, and the startup validation that refuses a bad range.</summary>
public sealed class OpcoesSessaoPafTests
{
    [Fact]
    public void SemConfiguracao_UsaOsValoresIniciais()
    {
        var opcoes = Resolver([]);

        opcoes.CarenciaReconexao.ShouldBe(TimeSpan.FromMinutes(2));
        opcoes.PrazoMarca.ShouldBe(TimeSpan.FromMinutes(10));
        opcoes.IntervaloRenovacao.ShouldBe(TimeSpan.FromMinutes(1));
        opcoes.Passo.ShouldBe(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void CarenciaDeTrintaSegundos_VemDaConfiguracao_EEncurtaOPasso()
    {
        var opcoes = Resolver(new() { ["PAF:Sessao:CarenciaReconexao"] = "00:00:30" });

        opcoes.CarenciaReconexao.ShouldBe(TimeSpan.FromSeconds(30));
        opcoes.Passo.ShouldBe(TimeSpan.FromSeconds(15), "a soltura não pode esperar um intervalo inteiro depois da carência");
    }

    [Theory]
    [InlineData("PAF:Sessao:CarenciaReconexao", "00:00:00", "CarenciaReconexao precisa ser positiva")]
    [InlineData("PAF:Sessao:CarenciaReconexao", "-00:00:30", "CarenciaReconexao precisa ser positiva")]
    [InlineData("PAF:Sessao:CarenciaReconexao", "00:10:01", "CarenciaReconexao não pode passar do PrazoMarca")]
    [InlineData("PAF:Sessao:IntervaloRenovacao", "00:00:00", "IntervaloRenovacao precisa ser positivo")]
    [InlineData("PAF:Sessao:IntervaloRenovacao", "00:10:00", "IntervaloRenovacao precisa ser menor que o PrazoMarca")]
    public void ValorForaDaFaixa_FalhaNaInicializacao(string chave, string valor, string mensagem)
    {
        using var servicos = Montar(new() { [chave] = valor });

        var erro = Should.Throw<OptionsValidationException>(() => servicos.GetRequiredService<IStartupValidator>().Validate());

        erro.Message.ShouldContain(mensagem);
    }

    private static OpcoesSessaoPaf Resolver(Dictionary<string, string?> valores)
    {
        using var servicos = Montar(valores);
        servicos.GetRequiredService<IStartupValidator>().Validate();
        return servicos.GetRequiredService<IOptions<OpcoesSessaoPaf>>().Value;
    }

    private static ServiceProvider Montar(Dictionary<string, string?> valores)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(valores).Build());
        OpcoesSessaoPaf.Registrar(services);
        return services.BuildServiceProvider();
    }
}
