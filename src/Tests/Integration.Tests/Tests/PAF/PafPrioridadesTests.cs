using System.Text;
using FSH.Modules.PAF.Contracts.v1.Estacoes;
using FSH.Modules.PAF.Contracts.v1.Prioridades;
using Integration.Tests.Infrastructure;
using Mediator;

namespace Integration.Tests.Tests.PAF;

/// <summary>
/// Prioridade cadastro (RF05, C14) through the HTTP pipeline the screen uses: what the API refuses, and what
/// deactivating does to the totem and to the fichas already emitted (seção 4).
/// </summary>
[Collection(FshCollectionDefinition.Name)]
public sealed class PafPrioridadesTests(FshWebApplicationFactory factory) : IAsyncLifetime
{
    private const string RotaConfiguracao = "/api/v1/paf/estacao/emissao/configuracao";

    private readonly PafApi _api = new(factory);
    private readonly PafTenantCliente _tenant = new(factory, "paf-pri");
    private PafOperacao _op = default!;

    public async Task InitializeAsync()
    {
        await _tenant.InicializarAsync();
        _op = new PafOperacao(factory, _tenant);
    }

    public Task DisposeAsync() => _op.DrenarCaixaDeSaidaAsync();

    [Theory]
    [InlineData("""{"nome":"","valor":1}""")]
    [InlineData("""{"nome":"   ","valor":1}""")]
    [InlineData("""{"nome":"Idoso","valor":1.5}""")]
    [InlineData("""{"nome":"Idoso","valor":"abc"}""")]
    [InlineData("""{"nome":"Idoso","valor":2147483648}""")]
    public async Task Criar_NomeVazioOuValorNaoInteiro_400ENadaGravado(string corpo)
    {
        using var root = await _api.RootAsync();
        var unidade = await _api.CriarUnidadeAsync(root);

        using var resposta = await EnviarJsonAsync(root, HttpMethod.Post, "/prioridades", unidade, corpo);

        resposta.StatusCode.ShouldBe(HttpStatusCode.BadRequest, await resposta.Content.ReadAsStringAsync());
        (await PafApi.PrioridadesAsync(root, unidade)).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("""{"nome":"","valor":5}""")]
    [InlineData("""{"nome":"Idoso","valor":5.5}""")]
    [InlineData("""{"nome":"Idoso","valor":null}""")]
    public async Task Editar_NomeVazioOuValorNaoInteiro_400ENadaMuda(string corpo)
    {
        using var root = await _api.RootAsync();
        var unidade = await _api.CriarUnidadeAsync(root);
        var normal = await PafApi.CriarPrioridadeAsync(root, unidade, "Normal");

        using var resposta = await EnviarJsonAsync(root, HttpMethod.Put, $"/prioridades/{normal}", unidade, corpo);

        resposta.StatusCode.ShouldBe(HttpStatusCode.BadRequest, await resposta.Content.ReadAsStringAsync());
        var gravada = (await PafApi.PrioridadesAsync(root, unidade)).ShouldHaveSingleItem();
        (gravada.Nome, gravada.Valor).ShouldBe(("Normal", 0));
    }

    [Fact]
    public async Task Editar_NomeEValor_ListaPeloValorMaiorPrimeiro()
    {
        using var root = await _api.RootAsync();
        var unidade = await _api.CriarUnidadeAsync(root);
        var normal = await PafApi.CriarPrioridadeAsync(root, unidade, "Normal");
        var idoso = await PafApi.OkAsync<Guid>(root, HttpMethod.Post, "/prioridades", unidade, new CreatePrioridadeCommand("Idoso", -1));

        await PafApi.OkAsync<Guid>(root, HttpMethod.Put, $"/prioridades/{idoso}", unidade, new UpdatePrioridadeCommand(idoso, "  Prioritário 80+ ", 20));

        var lista = await PafApi.PrioridadesAsync(root, unidade);
        lista.Select(p => (p.Id, p.Nome, p.Valor)).ShouldBe([(idoso, "Prioritário 80+", 20), (normal, "Normal", 0)]);
    }

    [Fact]
    public async Task Desativar_SomeDoTotem_FichaJaEmitidaContinuaChamavelPeloValorMaior_EReativarDevolveOBotao()
    {
        var c = await _op.CriarAsync();
        using var totem = await _op.TotemAsync(c);
        var normal = await PafOperacao.EmitirOkAsync(totem, c.Caixa, c.Normal);
        var preferencial = await PafOperacao.EmitirOkAsync(totem, c.Caixa, c.Preferencial);
        await _op.EnvelhecerFichasAsync(c.UnidadeId);

        // Through the handler: switching a person's current unidade needs Valkey, which this collection has not.
        // The HTTP path of disable/enable is the walk's (clients/dashboard/tests/e2e/paf-prioridades.walk.mjs).
        await EnviarAsync(c, new DisablePrioridadeCommand(c.Preferencial));

        (await BotoesAsync(totem)).ShouldBe([(c.Caixa, c.Normal), (c.Protocolo, c.Normal)]);
        using (var recusada = await PafOperacao.EmitirAsync(totem, c.Caixa, c.Preferencial, Guid.NewGuid().ToString("N")))
        {
            recusada.StatusCode.ShouldBe(HttpStatusCode.Conflict, "deactivation blocks new emissions");
        }

        // Emitted second and now inactive, the Preferencial ficha is still the first called: value 10 beats 0.
        using var atendente = await _op.AtendenteAsync(c, c.GuicheA);
        var primeira = await PafOperacao.ChamarOkAsync(atendente);
        (primeira.FichaId, primeira.PrioridadeId).ShouldBe((preferencial.FichaId, c.Preferencial));
        await PafOperacao.AgirOkAsync(atendente, primeira.AtendimentoId, "iniciar");
        await PafOperacao.AgirOkAsync(atendente, primeira.AtendimentoId, "encerrar");
        (await PafOperacao.ChamarOkAsync(atendente)).FichaId.ShouldBe(normal.FichaId);

        await EnviarAsync(c, new EnablePrioridadeCommand(c.Preferencial));
        (await BotoesAsync(totem)).ShouldContain((c.Caixa, c.Preferencial));
    }

    private async Task EnviarAsync(CenarioOperacao c, ICommand<Guid> command)
    {
        using var scope = factory.Services.CreateScope();
        _tenant.Contexto(scope, c.UnidadeId);
        await scope.ServiceProvider.GetRequiredService<IMediator>().Send(command);
    }

    private static async Task<HttpResponseMessage> EnviarJsonAsync(HttpClient client, HttpMethod metodo, string rota, Guid unidade, string json)
    {
        using var request = new HttpRequestMessage(metodo, $"{PafApi.Base}{rota}");
        request.Headers.Add(PafApi.CabecalhoUnidade, unidade.ToString());
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        return await client.SendAsync(request);
    }

    private static async Task<List<(Guid FilaId, Guid PrioridadeId)>> BotoesAsync(HttpClient totem)
    {
        using var resposta = await totem.GetAsync(RotaConfiguracao);
        resposta.StatusCode.ShouldBe(HttpStatusCode.OK, await resposta.Content.ReadAsStringAsync());
        var cfg = await resposta.Content.ReadFromJsonAsync<ConfiguracaoEstacaoDto>();
        return cfg!.Botoes.OrderBy(b => b.Ordem).Select(b => (b.FilaId, b.PrioridadeId)).ToList();
    }
}
