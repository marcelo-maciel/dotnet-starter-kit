using FSH.Modules.PAF.Contracts.v1.Contratos;
using FSH.Modules.PAF.Contracts.v1.Estacoes;
using FSH.Modules.PAF.Contracts.v1.Filas;
using FSH.Modules.PAF.Contracts.v1.UsuariosUnidade;
using Integration.Tests.Infrastructure;

namespace Integration.Tests.Tests.PAF;

/// <summary>Cadastro rules of RF02, RF04, RF05 and RF23 through the HTTP pipeline, failure paths included.</summary>
[Collection(FshCollectionDefinition.Name)]
public sealed class PafCadastroTests(FshWebApplicationFactory factory)
{
    private readonly PafApi _api = new(factory);

    [Fact]
    public async Task LetraRepetidaNaUnidade_EmOutraFila_Recusada409ComMensagem_InclusiveEmMinuscula()
    {
        using var root = await _api.RootAsync();
        var unidade = await _api.CriarUnidadeAsync(root);
        var normal = await PafApi.CriarPrioridadeAsync(root, unidade, "Normal");
        await PafApi.CriarFilaAsync(root, unidade, "Caixa", normal, "A");

        using var response = await PafApi.EnviarAsync(root, HttpMethod.Post, "/filas", unidade,
            new CreateFilaCommand("Protocolo", "Protocolo", null, null, [new FilaPrioridadeItem(normal, "a", 1, 0)]));

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await response.Content.ReadAsStringAsync()).ShouldContain("A letra A já é usada na fila Caixa");
        (await PafApi.FilasAsync(root, unidade)).Count.ShouldBe(1, "the refused fila must not be half-written");
    }

    [Fact]
    public async Task MesmaLetraEmOutraUnidade_Aceita()
    {
        using var root = await _api.RootAsync();
        var u1 = await _api.CriarUnidadeAsync(root);
        var u2 = await _api.CriarUnidadeAsync(root);

        await PafApi.CriarFilaAsync(root, u1, "Caixa", await PafApi.CriarPrioridadeAsync(root, u1, "Normal"), "A");
        await PafApi.CriarFilaAsync(root, u2, "Caixa", await PafApi.CriarPrioridadeAsync(root, u2, "Normal"), "A");

        (await PafApi.FilasAsync(root, u2)).Single().Prioridades.Single().Letra.ShouldBe("A");
    }

    [Fact]
    public async Task LetraDisputadaEmParalelo_UmaGravaEAOutraRecebe409_NuncaDuasLinhas()
    {
        using var root = await _api.RootAsync();
        var unidade = await _api.CriarUnidadeAsync(root);
        var normal = await PafApi.CriarPrioridadeAsync(root, unidade, "Normal");
        var idoso = await PafApi.CriarPrioridadeAsync(root, unidade, "Idoso");
        var caixa = await PafApi.CriarFilaAsync(root, unidade, "Caixa", normal, "A");
        var protocolo = await PafApi.CriarFilaAsync(root, unidade, "Protocolo", normal, "B");

        var tentativas = await Task.WhenAll(
            PafApi.EnviarAsync(root, HttpMethod.Post, $"/filas/{caixa}/prioridades", unidade, new AddFilaPrioridadeCommand(caixa, idoso, "Z", 1, 1)),
            PafApi.EnviarAsync(root, HttpMethod.Post, $"/filas/{protocolo}/prioridades", unidade, new AddFilaPrioridadeCommand(protocolo, idoso, "Z", 1, 1)));
        try
        {
            tentativas.Count(r => r.StatusCode == HttpStatusCode.OK).ShouldBe(1);
            tentativas.Count(r => r.StatusCode == HttpStatusCode.Conflict).ShouldBe(1);
        }
        finally
        {
            foreach (var r in tentativas)
            {
                r.Dispose();
            }
        }

        var filas = await PafApi.FilasAsync(root, unidade);
        filas.SelectMany(f => f.Prioridades).Count(p => p.Letra == "Z").ShouldBe(1);
    }

    [Fact]
    public async Task FilaSemPrioridade_OuJanelaQueViraAMeiaNoite_Recusada400()
    {
        using var root = await _api.RootAsync();
        var unidade = await _api.CriarUnidadeAsync(root);
        var normal = await PafApi.CriarPrioridadeAsync(root, unidade, "Normal");

        using var semPrioridade = await PafApi.EnviarAsync(root, HttpMethod.Post, "/filas", unidade,
            new CreateFilaCommand("Caixa", "Caixa", null, null, []));
        semPrioridade.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        using var janelaNoturna = await PafApi.EnviarAsync(root, HttpMethod.Post, "/filas", unidade,
            new CreateFilaCommand("Caixa", "Caixa", new TimeOnly(22, 0), new TimeOnly(2, 0), [new FilaPrioridadeItem(normal, "A", 1, 0)]));
        janelaNoturna.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        using var soUmaHora = await PafApi.EnviarAsync(root, HttpMethod.Post, "/filas", unidade,
            new CreateFilaCommand("Caixa", "Caixa", new TimeOnly(8, 0), null, [new FilaPrioridadeItem(normal, "A", 1, 0)]));
        soUmaHora.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        (await PafApi.FilasAsync(root, unidade)).ShouldBeEmpty();
    }

    [Fact]
    public async Task PrioridadeDeOutraUnidade_NaoEntraNaGradeDaFila()
    {
        using var root = await _api.RootAsync();
        var u1 = await _api.CriarUnidadeAsync(root);
        var u2 = await _api.CriarUnidadeAsync(root);
        var deU2 = await PafApi.CriarPrioridadeAsync(root, u2, "Normal");

        using var response = await PafApi.EnviarAsync(root, HttpMethod.Post, "/filas", u1,
            new CreateFilaCommand("Caixa", "Caixa", null, null, [new FilaPrioridadeItem(deU2, "A", 1, 0)]));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task DesativarEAtivarFila_MudaSoOIndicador()
    {
        using var root = await _api.RootAsync();
        var unidade = await _api.CriarUnidadeAsync(root);
        var fila = await PafApi.CriarFilaAsync(root, unidade, "Caixa", await PafApi.CriarPrioridadeAsync(root, unidade, "Normal"), "A");

        await PafApi.OkAsync<Guid>(root, HttpMethod.Post, $"/filas/{fila}/disable", unidade, null);
        (await PafApi.FilasAsync(root, unidade)).Single().Ativa.ShouldBeFalse();

        await PafApi.OkAsync<Guid>(root, HttpMethod.Post, $"/filas/{fila}/enable", unidade, null);
        (await PafApi.FilasAsync(root, unidade)).Single().Ativa.ShouldBeTrue();

        // Another unidade's header cannot reach this fila: the unit filter hides it.
        var outra = await _api.CriarUnidadeAsync(root);
        using var deFora = await PafApi.EnviarAsync(root, HttpMethod.Post, $"/filas/{fila}/disable", outra, null);
        deFora.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Estacao_GuicheSemFilaDeGuiche_PapelForaDoTipo_EFilaDeOutraUnidade_Recusados()
    {
        using var root = await _api.RootAsync();
        var unidade = await _api.CriarUnidadeAsync(root);
        var normal = await PafApi.CriarPrioridadeAsync(root, unidade, "Normal");
        var fila = await PafApi.CriarFilaAsync(root, unidade, "Caixa", normal, "A");
        var outra = await _api.CriarUnidadeAsync(root);
        var filaDeOutra = await PafApi.CriarFilaAsync(root, outra, "Caixa", await PafApi.CriarPrioridadeAsync(root, outra, "Normal"), "A");

        using var muda = await PafApi.EnviarAsync(root, HttpMethod.Post, "/estacoes", unidade,
            new CreateEstacaoCommand("Guichê 1", "Guiche", [new EstacaoFilaItem("Emissao", fila, 0)], [new EstacaoPrioridadeItem(normal, 0)]));
        muda.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        using var semPrioridade = await PafApi.EnviarAsync(root, HttpMethod.Post, "/estacoes", unidade,
            new CreateEstacaoCommand("Guichê 1", "Guiche", [new EstacaoFilaItem("Guiche", fila, 0)], []));
        semPrioridade.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        using var tipoDesconhecido = await PafApi.EnviarAsync(root, HttpMethod.Post, "/estacoes", unidade,
            new CreateEstacaoCommand("Guichê 1", "GuicheComControleRemoto", [], []));
        tipoDesconhecido.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        using var deOutraUnidade = await PafApi.EnviarAsync(root, HttpMethod.Post, "/estacoes", unidade,
            new CreateEstacaoCommand("Guichê 1", "Guiche", [new EstacaoFilaItem("Guiche", filaDeOutra, 0)], [new EstacaoPrioridadeItem(normal, 0)]));
        deOutraUnidade.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        (await PafApi.OkAsync<List<EstacaoDto>>(root, HttpMethod.Get, "/estacoes", unidade, null)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Estacao_CriadaComListasPorPapel_EAtualizacaoSubstituiAsListas()
    {
        using var root = await _api.RootAsync();
        var unidade = await _api.CriarUnidadeAsync(root);
        var normal = await PafApi.CriarPrioridadeAsync(root, unidade, "Normal");
        var idoso = await PafApi.CriarPrioridadeAsync(root, unidade, "Idoso");
        var caixa = await PafApi.CriarFilaAsync(root, unidade, "Caixa", normal, "A");
        var protocolo = await PafApi.CriarFilaAsync(root, unidade, "Protocolo", idoso, "B");

        var id = await PafApi.OkAsync<Guid>(root, HttpMethod.Post, "/estacoes", unidade, new CreateEstacaoCommand(
            "Guichê e painel", "GuichePainel",
            [new EstacaoFilaItem("Guiche", caixa, 0), new EstacaoFilaItem("Painel", caixa, 0), new EstacaoFilaItem("Painel", protocolo, 1)],
            [new EstacaoPrioridadeItem(normal, 0)]));

        var criada = (await PafApi.OkAsync<List<EstacaoDto>>(root, HttpMethod.Get, "/estacoes", unidade, null)).Single(e => e.Id == id);
        criada.Tipo.ShouldBe("GuichePainel");
        criada.Filas.Count(f => f.Papel == "Painel").ShouldBe(2);
        criada.Filas.Single(f => f.Papel == "Guiche").FilaId.ShouldBe(caixa);

        await PafApi.OkAsync<Guid>(root, HttpMethod.Put, $"/estacoes/{id}", unidade, new UpdateEstacaoCommand(
            id, "Guichê 3", [new EstacaoFilaItem("Guiche", protocolo, 0), new EstacaoFilaItem("Guiche", caixa, 1)],
            [new EstacaoPrioridadeItem(idoso, 0), new EstacaoPrioridadeItem(normal, 1)]));

        var atualizada = (await PafApi.OkAsync<List<EstacaoDto>>(root, HttpMethod.Get, "/estacoes", unidade, null)).Single(e => e.Id == id);
        atualizada.Nome.ShouldBe("Guichê 3");
        atualizada.Filas.Select(f => (f.Papel, f.FilaId)).ShouldBe([("Guiche", protocolo), ("Guiche", caixa)]);
        atualizada.Prioridades.Select(p => p.PrioridadeId).ShouldBe([idoso, normal]);
    }

    [Fact]
    public async Task ContratoUnidade_OperadorRaizGravaEAtualiza_EAUnidadeLe()
    {
        using var root = await _api.RootAsync();
        var unidade = await _api.CriarUnidadeAsync(root);

        var id = await PafApi.OkAsync<Guid>(root, HttpMethod.Post, "/contratos", null,
            new CreateContratoUnidadeCommand(unidade, new DateOnly(2026, 1, 1), null, "Básico", 3, "Recusar"));
        await PafApi.OkAsync<Guid>(root, HttpMethod.Put, $"/contratos/{id}", null,
            new UpdateContratoUnidadeCommand(id, new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31), "Pro", 10, "UltrapassarComAviso"));

        var contratos = await PafApi.OkAsync<List<ContratoUnidadeDto>>(root, HttpMethod.Get, "/contratos", unidade, null);
        var contrato = contratos.Single();
        contrato.LimiteSessoes.ShouldBe(10);
        contrato.Politica.ShouldBe("UltrapassarComAviso");

        using var fimAntesDoInicio = await PafApi.EnviarAsync(root, HttpMethod.Post, "/contratos", null,
            new CreateContratoUnidadeCommand(unidade, new DateOnly(2026, 6, 1), new DateOnly(2026, 1, 1), "Pro", 1, "Recusar"));
        fimAntesDoInicio.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        using var unidadeInexistente = await PafApi.EnviarAsync(root, HttpMethod.Post, "/contratos", null,
            new CreateContratoUnidadeCommand(Guid.NewGuid(), new DateOnly(2026, 1, 1), null, "Pro", 1, "Recusar"));
        unidadeInexistente.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task EscopoDoUsuario_ReordenaPelaLista_ERecusaUnidadeForaDoEscopoDeQuemConcede()
    {
        using var root = await _api.RootAsync();
        var adminId = await _api.RootAdminIdAsync();
        var u1 = await _api.CriarUnidadeAsync(root);
        var u2 = await _api.CriarUnidadeAsync(root);

        var atual = await PafApi.OkAsync<List<UsuarioUnidadeDto>>(root, HttpMethod.Get, $"/usuarios/{adminId}/unidades", null, null);
        var reordenada = atual.Select(u => u.UnidadeId).Where(u => u != u2).Prepend(u2).ToList();
        await PafApi.OkAsync<Guid>(root, HttpMethod.Put, $"/usuarios/{adminId}/unidades", null, new SetUsuarioUnidadesCommand(adminId, reordenada));

        var depois = await PafApi.OkAsync<List<UsuarioUnidadeDto>>(root, HttpMethod.Get, $"/usuarios/{adminId}/unidades", null, null);
        depois[0].ShouldBe(new UsuarioUnidadeDto(u2, 0));
        depois.ShouldContain(new UsuarioUnidadeDto(u1, reordenada.IndexOf(u1)));

        // Root holds Unidades.Create, so it grants any active unidade of the tenant; one that does not exist is
        // refused. The subset rule for callers without it is in PafEscopoConcessaoTests.
        using var foraDoEscopo = await PafApi.EnviarAsync(root, HttpMethod.Put, $"/usuarios/{adminId}/unidades", null,
            new SetUsuarioUnidadesCommand(adminId, [.. reordenada, Guid.NewGuid()]));
        foraDoEscopo.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        using var vazia = await PafApi.EnviarAsync(root, HttpMethod.Put, $"/usuarios/{adminId}/unidades", null,
            new SetUsuarioUnidadesCommand(adminId, []));
        vazia.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        using var usuarioInexistente = await PafApi.EnviarAsync(root, HttpMethod.Put, $"/usuarios/{Guid.NewGuid()}/unidades", null,
            new SetUsuarioUnidadesCommand(Guid.Empty, [u1]));
        usuarioInexistente.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }
}
