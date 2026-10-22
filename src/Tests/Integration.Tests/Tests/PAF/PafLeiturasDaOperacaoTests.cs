using FSH.Modules.PAF.Contracts.v1.Estacoes;
using FSH.Modules.PAF.Contracts.v1.Guiches;
using Integration.Tests.Infrastructure;

namespace Integration.Tests.Tests.PAF;

/// <summary>
/// Reads the operation screens depend on, through the real host: the totem's buttons from its own credential
/// (RF04, RF11) and the guichê screen of the ordinary attendant, who holds no cadastro permission (seção 5).
/// </summary>
[Collection(FshCollectionDefinition.Name)]
public sealed class PafLeiturasDaOperacaoTests(FshWebApplicationFactory factory) : IAsyncLifetime
{
    private const string RotaConfiguracaoEmissao = "/api/v1/paf/estacao/emissao/configuracao";
    private const string RotaConfiguracaoPainel = "/api/v1/paf/estacao/painel/configuracao";
    private const string RotaUnidades = "/api/v1/paf/guiche/unidades";
    private const string RotaGuiches = "/api/v1/paf/guiche/guiches";
    private const string RotaPosto = "/api/v1/paf/guiche/posto";

    private readonly PafTenantCliente _tenant = new(factory, "paf-leit");
    private PafOperacao _op = default!;

    public async Task InitializeAsync()
    {
        await _tenant.InicializarAsync();
        _op = new PafOperacao(factory, _tenant);
    }

    public Task DisposeAsync() => _op.DrenarCaixaDeSaidaAsync();

    [Fact]
    public async Task Totem_LeBotoesDasFilasDaEstacao_NaOrdemESoComPrioridadeAtiva()
    {
        var c = await _op.CriarAsync();
        using var totem = await _op.TotemAsync(c);

        var botoes = (await LerOkAsync<ConfiguracaoEstacaoDto>(totem, RotaConfiguracaoEmissao)).Botoes;

        // Station order (Caixa 0, Protocolo 1), then the fila's prioridade order; Retaguarda is on no totem.
        botoes.Select(b => (b.FilaId, b.PrioridadeId, b.Letra, b.Ordem)).ShouldBe(
        [
            (c.Caixa, c.Normal, "A", 0),
            (c.Caixa, c.Preferencial, "AP", 1),
            (c.Protocolo, c.Normal, "B", 2),
        ]);
        botoes[0].Fila.ShouldBe("Caixa");
        botoes[0].Prioridade.ShouldBe("Normal");

        // Every button emits: the read follows the same rule the emission enforces.
        foreach (var b in botoes)
        {
            await PafOperacao.EmitirOkAsync(totem, b.FilaId, b.PrioridadeId);
        }

        await _tenant.ExecutarAsync($"""UPDATE paf."Prioridades" SET "Ativa" = false WHERE "Id" = {c.Preferencial}""");
        await _tenant.ExecutarAsync($"""UPDATE paf."Filas" SET "Ativa" = false WHERE "Id" = {c.Protocolo}""");
        var restantes = (await LerOkAsync<ConfiguracaoEstacaoDto>(totem, RotaConfiguracaoEmissao)).Botoes;
        restantes.Select(b => (b.FilaId, b.PrioridadeId, b.Ordem)).ShouldBe([(c.Caixa, c.Normal, 0)]);
    }

    [Fact]
    public async Task Painel_LeConfiguracaoSemBotoes()
    {
        var c = await _op.CriarAsync();
        using var painel = _tenant.Cliente(await _op.CredencialAsync(c.UnidadeId, c.Painel, FSH.Modules.PAF.Contracts.Authorization.PafPapeisEstacao.Painel));

        (await LerOkAsync<ConfiguracaoEstacaoDto>(painel, RotaConfiguracaoPainel)).Botoes.ShouldBeEmpty();
    }

    [Fact]
    public async Task AtendenteBasico_SemPermissaoDeCadastro_LeUnidadesGuichesEPosto_EOpera()
    {
        var c = await _op.CriarAsync();
        var alheia = await _op.CriarAsync();
        using var totem = await _op.TotemAsync(c);
        var usuario = await _tenant.CriarUsuarioAsync(c.UnidadeId);
        using var atendente = _tenant.Cliente((await _tenant.EntrarAsync(usuario)).AccessToken);

        // The kit's basic role: no cadastro read, which is why these routes exist.
        using (var cadastro = await atendente.GetAsync("/api/v1/paf/unidades"))
        {
            cadastro.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }

        using (var estacoes = await atendente.GetAsync("/api/v1/paf/estacoes"))
        {
            estacoes.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }

        var unidades = await LerOkAsync<List<UnidadeDoGuicheDto>>(atendente, RotaUnidades);
        unidades.Select(u => u.Id).ShouldBe([c.UnidadeId]);

        var posto = await LerOkAsync<PostoGuicheDto>(atendente, RotaPosto);
        posto.UnidadeId.ShouldBe(c.UnidadeId);
        posto.EstacaoId.ShouldBeNull();
        posto.Atendimento.ShouldBeNull();

        var guiches = await LerOkAsync<List<GuicheDaUnidadeDto>>(atendente, RotaGuiches);
        guiches.Select(g => g.Id).ShouldBe([c.GuicheA, c.GuicheB], ignoreOrder: true);
        guiches.ShouldAllBe(g => g.Ocupante == null && !g.Meu);

        using (var selecao = await atendente.PostAsJsonAsync("/api/v1/paf/sessao/guiche", new { estacaoId = c.GuicheA }))
        {
            selecao.StatusCode.ShouldBe(HttpStatusCode.NoContent, await selecao.Content.ReadAsStringAsync());
        }

        await PafOperacao.EmitirOkAsync(totem, c.Caixa, c.Normal);
        await _op.EnvelhecerFichasAsync(c.UnidadeId);
        var chamada = await PafOperacao.ChamarOkAsync(atendente);

        // A reload asks the server: unit, guichê and the called atendimento come back.
        posto = await LerOkAsync<PostoGuicheDto>(atendente, RotaPosto);
        posto.EstacaoId.ShouldBe(c.GuicheA);
        posto.Guiche.ShouldBe("Guichê 1");
        posto.Atendimento.ShouldNotBeNull();
        posto.Atendimento.AtendimentoId.ShouldBe(chamada.AtendimentoId);
        posto.Atendimento.Senha.ShouldBe(chamada.Senha);
        posto.Atendimento.Estado.ShouldBe("Chamado");
        posto.Atendimento.Versao.ShouldBe(chamada.Versao);

        await PafOperacao.AgirOkAsync(atendente, chamada.AtendimentoId, "iniciar");
        (await LerOkAsync<PostoGuicheDto>(atendente, RotaPosto)).Atendimento!.Estado.ShouldBe("EmAtendimento");
        await PafOperacao.AgirOkAsync(atendente, chamada.AtendimentoId, "encerrar");
        (await LerOkAsync<PostoGuicheDto>(atendente, RotaPosto)).Atendimento.ShouldBeNull();

        // A unit outside the scope stays invisible even when asked for by header: the session's unit wins.
        using var pedido = new HttpRequestMessage(HttpMethod.Get, RotaGuiches);
        pedido.Headers.Add("X-Paf-Unidade", alheia.UnidadeId.ToString());
        using var resposta = await atendente.SendAsync(pedido);
        resposta.StatusCode.ShouldBe(HttpStatusCode.OK);
        var vistos = (await resposta.Content.ReadFromJsonAsync<List<GuicheDaUnidadeDto>>())!;
        vistos.Select(g => g.Id).ShouldBe([c.GuicheA, c.GuicheB], ignoreOrder: true);

        // Switching to a unit outside the scope is refused, so its guichês can never be listed.
        using var troca = await atendente.PostAsJsonAsync("/api/v1/paf/sessao/unidade-corrente", new { unidadeId = alheia.UnidadeId });
        troca.StatusCode.ShouldBe(HttpStatusCode.Forbidden, await troca.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Guiches_InformaQuemOcupaCadaUm()
    {
        var c = await _op.CriarAsync();
        var colega = await _tenant.CriarUsuarioAsync(c.UnidadeId);
        using var clienteColega = _tenant.Cliente((await _tenant.EntrarAsync(colega)).AccessToken);
        using (var selecao = await clienteColega.PostAsJsonAsync("/api/v1/paf/sessao/guiche", new { estacaoId = c.GuicheA }))
        {
            selecao.StatusCode.ShouldBe(HttpStatusCode.NoContent, await selecao.Content.ReadAsStringAsync());
        }

        using var atendente = await _op.AtendenteAsync(c, c.GuicheB);

        var guiches = await LerOkAsync<List<GuicheDaUnidadeDto>>(atendente, RotaGuiches);
        var a = guiches.Single(g => g.Id == c.GuicheA);
        var b = guiches.Single(g => g.Id == c.GuicheB);
        // The seeder registers userName as the e-mail's local part.
        a.Ocupante.ShouldBe(colega.Email.Split('@')[0]);
        a.Meu.ShouldBeFalse();
        b.Ocupante.ShouldBeNull();
        b.Meu.ShouldBeTrue();
    }

    [Fact]
    public async Task UnidadesDoGuiche_SoAtivasDoEscopo_SoIdENome()
    {
        var ativa = await _tenant.CriarUnidadeAsync(limiteSessoes: 5);
        var inativa = await _tenant.CriarUnidadeAsync(limiteSessoes: 5);
        await _tenant.CriarUnidadeAsync(limiteSessoes: 5); // active, outside the user's scope
        var usuario = await _tenant.CriarUsuarioAsync(ativa.UnidadeId, inativa.UnidadeId);
        using var atendente = _tenant.Cliente((await _tenant.EntrarAsync(usuario)).AccessToken);
        await _tenant.ExecutarAsync($"""UPDATE paf."Unidades" SET "Ativa" = false WHERE "Id" = {inativa.UnidadeId}""");

        using var resposta = await atendente.GetAsync(RotaUnidades);
        resposta.StatusCode.ShouldBe(HttpStatusCode.OK, await resposta.Content.ReadAsStringAsync());
        using var json = System.Text.Json.JsonDocument.Parse(await resposta.Content.ReadAsStringAsync());

        var itens = json.RootElement.EnumerateArray().ToList();
        itens.Select(i => i.GetProperty("id").GetGuid()).ShouldBe([ativa.UnidadeId]);
        itens.Single().EnumerateObject().Select(p => p.Name).ShouldBe(["id", "nome"], ignoreOrder: true);
    }

    private static async Task<T> LerOkAsync<T>(HttpClient cliente, string rota)
    {
        using var resposta = await cliente.GetAsync(rota);
        resposta.StatusCode.ShouldBe(HttpStatusCode.OK, await resposta.Content.ReadAsStringAsync());
        return (await resposta.Content.ReadFromJsonAsync<T>())!;
    }
}
