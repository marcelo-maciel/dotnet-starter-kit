using FSH.Modules.PAF.Contracts.v1.Estacoes;
using FSH.Modules.PAF.Contracts.v1.Filas;
using FSH.Modules.PAF.Contracts.v1.Setores;
using Integration.Tests.Infrastructure;
using Mediator;

namespace Integration.Tests.Tests.PAF;

/// <summary>
/// Complete fila cadastro (RF02, C09): appearance with the RNF19 contrast floor, button color per prioridade, setor
/// (create, rename, list, unique name per unidade) and the window and letter rules not covered by <see cref="PafCadastroTests"/>.
/// </summary>
[Collection(FshCollectionDefinition.Name)]
public sealed class PafFilasSetoresTests(FshWebApplicationFactory factory)
{
    private const string Branco = "#FFFFFF";
    private const string Amarelo = "#FFFF00";
    private const string Preto = "#000000";
    private const string Azul = "#0B3D91";

    private readonly PafApi _api = new(factory);

    [Fact]
    public async Task JanelaVazia_LetraDeSeisCaracteres_ESoUmaHora_Recusadas400()
    {
        using var root = await _api.RootAsync();
        var unidade = await _api.CriarUnidadeAsync(root);
        var normal = await PafApi.CriarPrioridadeAsync(root, unidade, "Normal");

        using var janelaVazia = await PafApi.EnviarAsync(root, HttpMethod.Post, "/filas", unidade,
            Fila("Caixa", new TimeOnly(10, 0), new TimeOnly(10, 0), null, PafApi.SemAparencia, Item(normal, "A", null)));
        janelaVazia.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        using var soAFinal = await PafApi.EnviarAsync(root, HttpMethod.Post, "/filas", unidade,
            Fila("Caixa", null, new TimeOnly(18, 0), null, PafApi.SemAparencia, Item(normal, "A", null)));
        soAFinal.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        using var seisCaracteres = await PafApi.EnviarAsync(root, HttpMethod.Post, "/filas", unidade,
            Fila("Caixa", null, null, null, PafApi.SemAparencia, Item(normal, "ABCDEF", null)));
        seisCaracteres.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        (await PafApi.FilasAsync(root, unidade)).ShouldBeEmpty();

        // The limit itself is accepted: five characters, the legacy column width.
        await PafApi.OkAsync<Guid>(root, HttpMethod.Post, "/filas", unidade,
            Fila("Caixa", new TimeOnly(8, 0), new TimeOnly(17, 0), null, PafApi.SemAparencia, Item(normal, "ABCDE", null)));
    }

    [Fact]
    public async Task PrioridadeEFilaDesativadas_ContinuamBloqueandoALetra()
    {
        using var root = await _api.RootAsync();
        var unidade = await _api.CriarUnidadeAsync(root);
        var idoso = await PafApi.CriarPrioridadeAsync(root, unidade, "Idoso");
        var normal = await PafApi.CriarPrioridadeAsync(root, unidade, "Normal");
        var caixa = await PafApi.CriarFilaAsync(root, unidade, "Caixa", idoso, "I");
        await PafApi.OkAsync<Guid>(root, HttpMethod.Post, $"/prioridades/{idoso}/disable", unidade, null);
        await PafApi.OkAsync<Guid>(root, HttpMethod.Post, $"/filas/{caixa}/disable", unidade, null);

        using var response = await PafApi.EnviarAsync(root, HttpMethod.Post, "/filas", unidade,
            Fila("Protocolo", null, null, null, PafApi.SemAparencia, Item(normal, "I", null)));

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict, "fichas already emitted under the disabled line stay callable");
        (await response.Content.ReadAsStringAsync()).ShouldContain("A letra I já é usada na fila Caixa");
    }

    [Fact]
    public async Task Aparencia_GravadaELida_ComCorDoBotaoPorPrioridadeESetor()
    {
        using var root = await _api.RootAsync();
        var unidade = await _api.CriarUnidadeAsync(root);
        var normal = await PafApi.CriarPrioridadeAsync(root, unidade, "Normal");
        var idoso = await PafApi.CriarPrioridadeAsync(root, unidade, "Idoso");
        var atendimento = await PafApi.OkAsync<Guid>(root, HttpMethod.Post, "/setores", unidade, new CreateSetorCommand("  Atendimento "));

        var caixa = await PafApi.OkAsync<Guid>(root, HttpMethod.Post, "/filas", unidade,
            Fila("Caixa", null, null, atendimento, new AparenciaFilaDto(" Arial ", Branco, Azul), Item(normal, "A", Azul)));
        await PafApi.OkAsync<Guid>(root, HttpMethod.Post, $"/filas/{caixa}/prioridades", unidade,
            new AddFilaPrioridadeCommand(caixa, idoso, "AI", 1, 1, "#111111"));

        var fila = (await PafApi.FilasAsync(root, unidade)).Single();
        fila.SetorId.ShouldBe(atendimento);
        fila.Aparencia.ShouldBe(new AparenciaFilaDto("Arial", Branco, Azul));
        fila.Prioridades.Select(p => (p.PrioridadeId, p.CorBotao)).ShouldBe([(normal, Azul), (idoso, "#111111")]);
        (await SetoresAsync(root, unidade)).ShouldBe([new SetorDto(atendimento, unidade, "Atendimento")]);

        // Back to "Sem setor", no font and no selected color: the PUT carries the whole fila.
        await PafApi.OkAsync<Guid>(root, HttpMethod.Put, $"/filas/{caixa}", unidade,
            new UpdateFilaCommand(caixa, "Caixa", "Caixa", null, null, null, new AparenciaFilaDto(null, Branco, null)));
        fila = (await PafApi.FilasAsync(root, unidade)).Single();
        fila.SetorId.ShouldBeNull();
        fila.Aparencia.ShouldBe(new AparenciaFilaDto(null, Branco, null));
    }

    [Fact]
    public async Task BrancoSobreAmarelo_RecusadoEmTodaPortaDeEntrada()
    {
        using var root = await _api.RootAsync();
        var unidade = await _api.CriarUnidadeAsync(root);
        var normal = await PafApi.CriarPrioridadeAsync(root, unidade, "Normal");
        var idoso = await PafApi.CriarPrioridadeAsync(root, unidade, "Idoso");

        // Selected-fila button and prioridade button on creation.
        using var selecionado = await PafApi.EnviarAsync(root, HttpMethod.Post, "/filas", unidade,
            Fila("Caixa", null, null, null, new AparenciaFilaDto(null, Branco, Amarelo), Item(normal, "A", null)));
        selecionado.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await selecionado.Content.ReadAsStringAsync()).ShouldContain("Botão fila selecionada: #FFFFFF sobre #FFFF00 dá 1,07:1");

        using var daPrioridade = await PafApi.EnviarAsync(root, HttpMethod.Post, "/filas", unidade,
            Fila("Caixa", null, null, null, new AparenciaFilaDto(null, Branco, Azul), Item(normal, "A", Amarelo)));
        daPrioridade.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await daPrioridade.Content.ReadAsStringAsync()).ShouldContain("Botão Normal");

        // A button color with no font color to measure against.
        using var semFonte = await PafApi.EnviarAsync(root, HttpMethod.Post, "/filas", unidade,
            Fila("Caixa", null, null, null, new AparenciaFilaDto(null, null, null), Item(normal, "A", Azul)));
        semFonte.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        // Short form and alpha are refused before any contrast is measured.
        using var curta = await PafApi.EnviarAsync(root, HttpMethod.Post, "/filas", unidade,
            Fila("Caixa", null, null, null, new AparenciaFilaDto(null, "#FFF", null), Item(normal, "A", null)));
        curta.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        using var alfa = await PafApi.EnviarAsync(root, HttpMethod.Post, "/filas", unidade,
            Fila("Caixa", null, null, null, new AparenciaFilaDto(null, Preto, null), Item(normal, "A", "#FFFFFF80")));
        alfa.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await PafApi.FilasAsync(root, unidade)).ShouldBeEmpty();

        // The pair spans the fila and its grid: each side is checked against the committed other side.
        var caixa = await PafApi.OkAsync<Guid>(root, HttpMethod.Post, "/filas", unidade,
            Fila("Caixa", null, null, null, new AparenciaFilaDto(null, Preto, null), Item(normal, "A", Amarelo)));
        using var fonteBranca = await PafApi.EnviarAsync(root, HttpMethod.Put, $"/filas/{caixa}", unidade,
            new UpdateFilaCommand(caixa, "Caixa", "Caixa", null, null, null, new AparenciaFilaDto(null, Branco, null)));
        fonteBranca.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await fonteBranca.Content.ReadAsStringAsync()).ShouldContain("Botão Normal: #FFFFFF sobre #FFFF00");
        (await PafApi.FilasAsync(root, unidade)).Single().Aparencia.CorFonte.ShouldBe(Preto);
    }

    [Fact]
    public async Task CorDoBotaoNaGrade_ConfereContraAFonteGravadaDaFila()
    {
        using var root = await _api.RootAsync();
        var unidade = await _api.CriarUnidadeAsync(root);
        var normal = await PafApi.CriarPrioridadeAsync(root, unidade, "Normal");
        var idoso = await PafApi.CriarPrioridadeAsync(root, unidade, "Idoso");
        var caixa = await PafApi.OkAsync<Guid>(root, HttpMethod.Post, "/filas", unidade,
            Fila("Caixa", null, null, null, new AparenciaFilaDto(null, Branco, null), Item(normal, "A", null)));

        using var add = await PafApi.EnviarAsync(root, HttpMethod.Post, $"/filas/{caixa}/prioridades", unidade,
            new AddFilaPrioridadeCommand(caixa, idoso, "AI", 1, 1, Amarelo));
        add.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        using var update = await PafApi.EnviarAsync(root, HttpMethod.Put, $"/filas/{caixa}/prioridades/{normal}", unidade,
            new UpdateFilaPrioridadeCommand(caixa, normal, "A", 1, 0, Amarelo));
        update.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        (await PafApi.FilasAsync(root, unidade)).Single().Prioridades.ShouldHaveSingleItem().CorBotao.ShouldBeNull();

        await PafApi.OkAsync<Guid>(root, HttpMethod.Put, $"/filas/{caixa}/prioridades/{normal}", unidade,
            new UpdateFilaPrioridadeCommand(caixa, normal, "A", 1, 0, Azul));
        (await PafApi.FilasAsync(root, unidade)).Single().Prioridades.Single().CorBotao.ShouldBe(Azul);
    }

    [Fact]
    public async Task SetorDeOutraUnidade_404NaFilaENoRenomear()
    {
        using var root = await _api.RootAsync();
        var u1 = await _api.CriarUnidadeAsync(root);
        var u2 = await _api.CriarUnidadeAsync(root);
        var normal = await PafApi.CriarPrioridadeAsync(root, u1, "Normal");
        var deU2 = await PafApi.OkAsync<Guid>(root, HttpMethod.Post, "/setores", u2, new CreateSetorCommand("Cartório"));

        using var criar = await PafApi.EnviarAsync(root, HttpMethod.Post, "/filas", u1,
            Fila("Caixa", null, null, deU2, PafApi.SemAparencia, Item(normal, "A", null)));
        criar.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        var caixa = await PafApi.CriarFilaAsync(root, u1, "Caixa", normal, "A");
        using var mover = await PafApi.EnviarAsync(root, HttpMethod.Put, $"/filas/{caixa}", u1,
            new UpdateFilaCommand(caixa, "Caixa", "Caixa", null, null, deU2, PafApi.SemAparencia));
        mover.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await PafApi.FilasAsync(root, u1)).Single().SetorId.ShouldBeNull();

        using var renomear = await PafApi.EnviarAsync(root, HttpMethod.Put, $"/setores/{deU2}", u1, new UpdateSetorCommand(deU2, "Outro"));
        renomear.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await SetoresAsync(root, u1)).ShouldBeEmpty();
        (await SetoresAsync(root, u2)).Single().Nome.ShouldBe("Cartório");
    }

    [Fact]
    public async Task SetoresHomonimos_409NaUnidade_E200EmOutra_InclusiveAoRenomear()
    {
        using var root = await _api.RootAsync();
        var u1 = await _api.CriarUnidadeAsync(root);
        var u2 = await _api.CriarUnidadeAsync(root);
        await PafApi.OkAsync<Guid>(root, HttpMethod.Post, "/setores", u1, new CreateSetorCommand("Cartório"));
        var protocolo = await PafApi.OkAsync<Guid>(root, HttpMethod.Post, "/setores", u1, new CreateSetorCommand("Protocolo"));

        using var repetido = await PafApi.EnviarAsync(root, HttpMethod.Post, "/setores", u1, new CreateSetorCommand(" Cartório "));
        repetido.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await repetido.Content.ReadAsStringAsync()).ShouldContain("Já existe um setor com este nome nesta unidade.");

        using var renomeado = await PafApi.EnviarAsync(root, HttpMethod.Put, $"/setores/{protocolo}", u1, new UpdateSetorCommand(protocolo, "Cartório"));
        renomeado.StatusCode.ShouldBe(HttpStatusCode.Conflict);

        await PafApi.OkAsync<Guid>(root, HttpMethod.Post, "/setores", u2, new CreateSetorCommand("Cartório"));
        await PafApi.OkAsync<Guid>(root, HttpMethod.Put, $"/setores/{protocolo}", u1, new UpdateSetorCommand(protocolo, "Protocolo geral"));

        (await SetoresAsync(root, u1)).Select(s => s.Nome).ShouldBe(["Cartório", "Protocolo geral"]);
        (await SetoresAsync(root, u2)).Select(s => s.Nome).ShouldBe(["Cartório"]);

        using var vazio = await PafApi.EnviarAsync(root, HttpMethod.Post, "/setores", u1, new CreateSetorCommand("   "));
        vazio.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task SetorDisputadoEmParalelo_UmGravaEOOutroRecebe409_NuncaDuasLinhas()
    {
        using var root = await _api.RootAsync();
        var unidade = await _api.CriarUnidadeAsync(root);

        var tentativas = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ =>
            PafApi.EnviarAsync(root, HttpMethod.Post, "/setores", unidade, new CreateSetorCommand("Recepção"))));
        try
        {
            tentativas.Count(r => r.StatusCode == HttpStatusCode.OK).ShouldBe(1);
            tentativas.Count(r => r.StatusCode == HttpStatusCode.Conflict).ShouldBe(3, "a 500 means the unique violation was not translated");
        }
        finally
        {
            foreach (var r in tentativas)
            {
                r.Dispose();
            }
        }

        (await SetoresAsync(root, unidade)).ShouldHaveSingleItem().Nome.ShouldBe("Recepção");
    }

    private static Task<List<SetorDto>> SetoresAsync(HttpClient client, Guid unidade) =>
        PafApi.OkAsync<List<SetorDto>>(client, HttpMethod.Get, "/setores", unidade, null);

    private static FilaPrioridadeItem Item(Guid prioridade, string letra, string? corBotao) => new(prioridade, letra, 1, 0, corBotao);

    private static CreateFilaCommand Fila(
        string nome, TimeOnly? inicio, TimeOnly? fim, Guid? setor, AparenciaFilaDto aparencia, FilaPrioridadeItem item) =>
        new(nome, nome, inicio, fim, setor, aparencia, [item]);
}

/// <summary>The appearance reaches the totem: the buttons carry the fila's font and colors and the prioridade's color (RF02, RF04).</summary>
[Collection(FshCollectionDefinition.Name)]
public sealed class PafAparenciaTotemTests(FshWebApplicationFactory factory) : IAsyncLifetime
{
    private readonly PafTenantCliente _tenant = new(factory, "paf-apar");
    private PafOperacao _op = default!;

    public async Task InitializeAsync()
    {
        await _tenant.InicializarAsync();
        _op = new PafOperacao(factory, _tenant);
    }

    public Task DisposeAsync() => _op.DrenarCaixaDeSaidaAsync();

    [Fact]
    public async Task BotoesDoTotem_TrazemFonteECoresDaFilaEDaPrioridade()
    {
        var c = await _op.CriarAsync();
        await EnviarAsync(c, new UpdateFilaCommand(c.Caixa, "Caixa", "Caixa", null, null, null, new AparenciaFilaDto("Verdana", "#FFFFFF", "#0B3D91")));
        await EnviarAsync(c, new UpdateFilaPrioridadeCommand(c.Caixa, c.Normal, "A", 1, 0, "#14532D"));

        using var totem = await _op.TotemAsync(c);
        using var resposta = await totem.GetAsync("/api/v1/paf/estacao/emissao/configuracao");
        resposta.StatusCode.ShouldBe(HttpStatusCode.OK, await resposta.Content.ReadAsStringAsync());
        var botoes = (await resposta.Content.ReadFromJsonAsync<ConfiguracaoEstacaoDto>())!.Botoes;

        var normal = botoes.Single(b => b.FilaId == c.Caixa && b.PrioridadeId == c.Normal);
        (normal.Fonte, normal.CorFonte, normal.CorBotao, normal.CorBotaoSelecionado).ShouldBe(("Verdana", "#FFFFFF", "#14532D", "#0B3D91"));
        var preferencial = botoes.Single(b => b.FilaId == c.Caixa && b.PrioridadeId == c.Preferencial);
        preferencial.CorBotao.ShouldBeNull("no color = the station's default");
        botoes.Single(b => b.FilaId == c.Protocolo).CorFonte.ShouldBeNull();
    }

    private async Task EnviarAsync<T>(CenarioOperacao c, ICommand<T> command)
    {
        using var scope = factory.Services.CreateScope();
        _tenant.Contexto(scope, c.UnidadeId);
        await scope.ServiceProvider.GetRequiredService<IMediator>().Send(command);
    }
}
