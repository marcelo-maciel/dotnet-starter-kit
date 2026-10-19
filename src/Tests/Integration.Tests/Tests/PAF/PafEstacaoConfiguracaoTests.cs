using System.Net.Http.Json;
using FSH.Framework.Core.Exceptions;
using FSH.Modules.PAF.Contracts.Authorization;
using FSH.Modules.PAF.Contracts.v1.Estacoes;
using FSH.Modules.PAF.Contracts.v1.Painel;
using FSH.Modules.PAF.Contracts.v1.TempoReal;
using FSH.Modules.PAF.Domain;
using FSH.Modules.PAF.Parametros;
using Integration.Tests.Infrastructure;
using Mediator;
using Microsoft.AspNetCore.SignalR.Client;

namespace Integration.Tests.Tests.PAF;

/// <summary>
/// RF04 configuration blocks of the station cadastro (C04): rules by type, accessibility floor of RNF19 and D17, the
/// stored keys the operation reads, the guichê number on the painel, and concurrent edits of one station.
/// </summary>
[Collection(FshCollectionDefinition.Name)]
public sealed class PafEstacaoConfiguracaoTests(FshWebApplicationFactory factory) : IAsyncLifetime
{
    private static readonly TimeSpan Espera = TimeSpan.FromSeconds(5);

    private readonly PafTenantCliente _tenant = new(factory, "paf-cfg");
    private readonly PafApi _api = new(factory);
    private readonly List<HubConnection> _conexoes = [];
    private PafOperacao _op = default!;

    public async Task InitializeAsync()
    {
        await _tenant.InicializarAsync();
        _op = new PafOperacao(factory, _tenant);
    }

    public async Task DisposeAsync()
    {
        foreach (var conexao in _conexoes)
        {
            await conexao.DisposeAsync();
        }

        await _op.DrenarCaixaDeSaidaAsync();
    }

    // ─── contraste e cores ───────────────────────────────────────────

    [Fact]
    public async Task Contraste_777777SobreBrancoRecusado_767676Aceito()
    {
        var c = await _op.CriarAsync();

        var recusa = await RecusaAsync(c, Totem(c, "Totem claro", Emissao(fundo: "#FFFFFF", fonte: "#777777", descricao: "#000000")));
        recusa.ShouldContain(e => e.StartsWith("Emissao.CorFonteBotao sobre CorFundoBotao tem contraste 4.47", StringComparison.Ordinal));
        recusa.Count.ShouldBe(1, "a descrição preta sobre branco passa");

        var id = await EnviarAsync(c, Totem(c, "Totem limite", Emissao(fundo: "#FFFFFF", fonte: "#767676", descricao: "#767676")));
        var gravada = (await EstacoesAsync(c)).Single(e => e.Id == id);
        gravada.Configuracao!.Emissao!.CorFonteBotao.ShouldBe("#767676");
    }

    [Theory]
    [InlineData("#GGG")]
    [InlineData("#GGGGGG")]
    [InlineData("#FFF")]
    [InlineData("#767676FF")]
    [InlineData("767676")]
    public async Task Cor_ForaDoFormatoRRGGBB_Recusada(string cor)
    {
        var c = await _op.CriarAsync();
        var recusa = await RecusaAsync(c, Totem(c, "Totem", Emissao(fundo: "#FFFFFF", fonte: cor, descricao: "#000000")));
        recusa.ShouldContain("Emissao.CorFonteBotao deve estar no formato #RRGGBB.");
    }

    [Fact]
    public async Task Cores_SoParteDoBloco_Recusadas()
    {
        var c = await _op.CriarAsync();
        var bloco = new BlocoPainelDto(null, null, null, "#000000", "#FFFFFF", null, null, null);
        var recusa = await RecusaAsync(c, Painel(c, "Painel", bloco));
        recusa.ShouldHaveSingleItem().ShouldContain("informe todas as cores do bloco");
    }

    [Fact]
    public async Task Painel_CadaCorDeTextoMedidaContraOFundo()
    {
        var c = await _op.CriarAsync();
        var bloco = new BlocoPainelDto(null, null, null, "#000000", "#FFFFFF", "#FFFF00", "#333333", null);
        var recusa = await RecusaAsync(c, Painel(c, "Painel", bloco));
        recusa.ShouldHaveSingleItem().ShouldStartWith("Painel.CorFonteAlternada sobre CorFundo");
    }

    // ─── blocos por tipo ─────────────────────────────────────────────

    [Fact]
    public async Task GuichePainelComBlocoEmissao_EGuicheSemBlocoGuiche_Recusados()
    {
        var c = await _op.CriarAsync();

        var comEmissao = await RecusaAsync(c, new CreateEstacaoCommand("G e P", "GuichePainel", GuicheDe(c), [new EstacaoPrioridadeItem(c.Normal, 0)],
            null, new BlocosEstacaoDto(BlocosDeTeste.BlocoEmissao(), BlocosDeTeste.BlocoGuiche(1), BlocosDeTeste.BlocoPainel())));
        comEmissao.ShouldBe(["O tipo GuichePainel não tem o bloco Emissao."]);

        var semGuiche = await RecusaAsync(c, new CreateEstacaoCommand("G", "Guiche", GuicheDe(c), [new EstacaoPrioridadeItem(c.Normal, 0)],
            null, new BlocosEstacaoDto(null, null, null)));
        semGuiche.ShouldBe(["O tipo Guiche exige o bloco Guiche."]);

        var administracao = await RecusaAsync(c, new CreateEstacaoCommand("Adm", "Administracao", [], [],
            null, new BlocosEstacaoDto(null, null, BlocosDeTeste.BlocoPainel())));
        administracao.ShouldBe(["O tipo Administracao não tem o bloco Painel."]);

        (await _op.ContarAsync(c, db => db.Estacoes.Where(e => e.Nome == "G e P" || e.Nome == "G" || e.Nome == "Adm"))).ShouldBe(0);
    }

    [Fact]
    public async Task TrocarOTipo_NaoExisteNoUpdate_OTipoGravadoDecideOsBlocos()
    {
        var c = await _op.CriarAsync();
        // D25: the type is fixed; an update carrying the blocks of another type is judged by the stored type.
        var recusa = await RecusaAsync(c, new UpdateEstacaoCommand(c.GuicheA, "Guichê 1", GuicheDe(c), [new EstacaoPrioridadeItem(c.Normal, 0)],
            null, new BlocosEstacaoDto(null, null, BlocosDeTeste.BlocoPainel())));
        recusa.ShouldBe(["O tipo Guiche exige o bloco Guiche.", "O tipo Guiche não tem o bloco Painel."]);
        (await _op.ContarAsync(c, db => db.Estacoes.Where(e => e.Id == c.GuicheA && e.Tipo == TipoEstacao.Guiche))).ShouldBe(1);
    }

    // ─── limites ─────────────────────────────────────────────────────

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(600, true)]
    [InlineData(601, false)]
    public async Task TempoDeExibicao_Entre1e600(int segundos, bool aceito)
    {
        var c = await _op.CriarAsync();
        var comando = Painel(c, $"Painel {segundos}", new BlocoPainelDto(segundos, null, null, null, null, null, null, null));
        if (aceito)
        {
            var id = await EnviarAsync(c, comando);
            (await EstacoesAsync(c)).Single(e => e.Id == id).Configuracao!.Painel!.TempoExibicaoChamadaSegundos.ShouldBe(segundos);
        }
        else
        {
            (await RecusaAsync(c, comando)).ShouldBe(["Painel.TempoExibicaoChamadaSegundos deve estar entre 1 e 600 segundos."]);
        }
    }

    [Fact]
    public async Task PrazoDoCicloDeEmissao_EBotaoDoTotem_NosLimites()
    {
        var c = await _op.CriarAsync();
        var recusa = await RecusaAsync(c, Totem(c, "Totem", new BlocoEmissaoDto(null, null, 43, null, null, null, 601)));
        recusa.ShouldBe([
            "Emissao.TamanhoBotaoPx deve estar entre 44 e 400.",
            "Emissao.PrazoCicloEmissaoSegundos deve estar entre 1 e 600 segundos.",
        ]);

        await EnviarAsync(c, Totem(c, "Totem", new BlocoEmissaoDto(null, null, 44, null, null, null, 600)));
    }

    [Fact]
    public async Task FonteDoTotem_PisoDoParametro_MudaSemNovoBuild()
    {
        var c = await _op.CriarAsync();
        var piso = CatalogoParametros.FonteMinimaTotemPx.Padrao;
        piso.ShouldBe(24, "D17");

        (await RecusaAsync(c, Totem(c, "Totem", Fonte(piso - 1)))).ShouldBe([$"Emissao.TamanhoFontePx deve estar entre {piso} e 400."]);
        await EnviarAsync(c, Totem(c, "Totem 24", Fonte(piso)));

        await GravarParametroDeEmpresaAsync(c.UnidadeId, "30");
        (await RecusaAsync(c, Totem(c, "Totem", Fonte(29)))).ShouldBe(["Emissao.TamanhoFontePx deve estar entre 30 e 400."]);
        await EnviarAsync(c, Totem(c, "Totem 30", Fonte(30)));
    }

    [Fact]
    public async Task Ramal_GravadoELido_E21CaracteresRecusado()
    {
        var c = await _op.CriarAsync();
        var id = await EnviarAsync(c, Totem(c, "Totem", BlocosDeTeste.BlocoEmissao()) with { Ramal = " 2041 " });
        (await EstacoesAsync(c)).Single(e => e.Id == id).Ramal.ShouldBe("2041");

        var erro = await Should.ThrowAsync<FluentValidation.ValidationException>(() => EnviarAsync(c,
            Totem(c, "Totem", BlocosDeTeste.BlocoEmissao()) with { Ramal = new string('1', Estacao.RamalMaxLength + 1) }));
        erro.Errors.ShouldContain(e => e.PropertyName == "Ramal");
    }

    // ─── pela API ────────────────────────────────────────────────────

    [Fact]
    public async Task ChaveDesconhecida_400PelaApi_ENadaGravado()
    {
        using var root = await _api.RootAsync();
        var unidade = await _api.CriarUnidadeAsync(root);

        using var naRaiz = await PafApi.EnviarAsync(root, HttpMethod.Post, "/estacoes", unidade, new
        {
            nome = "Painel",
            tipo = "Painel",
            filas = Array.Empty<object>(),
            prioridades = Array.Empty<object>(),
            configuracao = new { painel = new { }, monitor = 2 },
        });
        naRaiz.StatusCode.ShouldBe(HttpStatusCode.BadRequest, await naRaiz.Content.ReadAsStringAsync());

        using var noBloco = await PafApi.EnviarAsync(root, HttpMethod.Post, "/estacoes", unidade, new
        {
            nome = "Painel",
            tipo = "Painel",
            filas = Array.Empty<object>(),
            prioridades = Array.Empty<object>(),
            configuracao = new { painel = new { displayDeDestino = 2 } },
        });
        noBloco.StatusCode.ShouldBe(HttpStatusCode.BadRequest, await noBloco.Content.ReadAsStringAsync());

        using var valida = await PafApi.EnviarAsync(root, HttpMethod.Post, "/estacoes", unidade, new
        {
            nome = "Painel válido",
            tipo = "Painel",
            filas = Array.Empty<object>(),
            prioridades = Array.Empty<object>(),
            ramal = "12",
            configuracao = new { painel = new { tempoExibicaoChamadaSegundos = 15 } },
        });
        valida.StatusCode.ShouldBe(HttpStatusCode.OK, await valida.Content.ReadAsStringAsync());

        var estacoes = await PafApi.OkAsync<List<EstacaoDto>>(root, HttpMethod.Get, "/estacoes", unidade, null);
        estacoes.ShouldHaveSingleItem().Nome.ShouldBe("Painel válido");
    }

    [Fact]
    public async Task ContrasteRecusado_Responde400ComOMotivo()
    {
        using var root = await _api.RootAsync();
        var unidade = await _api.CriarUnidadeAsync(root);
        using var resposta = await PafApi.EnviarAsync(root, HttpMethod.Post, "/estacoes", unidade,
            new CreateEstacaoCommand("Totem", "Emissao", [], [], null, new BlocosEstacaoDto(Emissao("#FFFFFF", "#777777", "#000000"), null, null)));
        resposta.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await resposta.Content.ReadAsStringAsync()).ShouldContain("Emissao.CorFonteBotao sobre CorFundoBotao");
    }

    // ─── chaves lidas pela operação ──────────────────────────────────

    [Fact]
    public async Task RespeitarOrdemFilas_GravadoPelaApi_MudaAOrdemDaChamada()
    {
        var c = await _op.CriarAsync();
        using var totem = await _op.TotemAsync(c);
        using var guicheB = await _op.AtendenteAsync(c, c.GuicheB);

        await PafOperacao.EmitirOkAsync(totem, c.Protocolo, c.Normal);
        var preferencial = await PafOperacao.EmitirOkAsync(totem, c.Caixa, c.Preferencial);
        await _op.EnvelhecerFichasAsync(c.UnidadeId);
        await AtualizarGuicheBAsync(c, respeitarOrdem: false);
        (await PafOperacao.ChamarOkAsync(guicheB)).AtendimentoId.ShouldBe(preferencial.AtendimentoId, "sem a opção, a prioridade maior vence");
        await EncerrarAsync(guicheB, preferencial.AtendimentoId);

        var outraPreferencial = await PafOperacao.EmitirOkAsync(totem, c.Caixa, c.Preferencial);
        await _op.EnvelhecerFichasAsync(c.UnidadeId);
        await AtualizarGuicheBAsync(c, respeitarOrdem: true);

        var segunda = await PafOperacao.ChamarOkAsync(guicheB);
        segunda.Senha.ShouldBe("B1", "Protocolo vem antes de Caixa no guichê B");
        segunda.AtendimentoId.ShouldNotBe(outraPreferencial.AtendimentoId);
    }

    [Fact]
    public async Task GuicheEPainel_CadastradoPelaApi_ChamarAnunciaONumeroNoPainel()
    {
        var c = await _op.CriarAsync();
        var bloco = new BlocoGuicheDto(12, "Caixa rápido", "Direita", RespeitarOrdemFilas: false, SelecionarFicha: false);
        var estacao = await EnviarAsync(c, new CreateEstacaoCommand("Mesa 12", "GuichePainel",
            [new EstacaoFilaItem("Guiche", c.Caixa, 0), new EstacaoFilaItem("Painel", c.Caixa, 0)],
            [new EstacaoPrioridadeItem(c.Normal, 0)], "3012", new BlocosEstacaoDto(null, bloco, BlocosDeTeste.BlocoPainel())));

        using var totem = await _op.TotemAsync(c);
        using var atendente = await _op.AtendenteAsync(c, estacao);
        var tokenPainel = await _op.CredencialAsync(c.UnidadeId, estacao, PafPapeisEstacao.Painel);
        using var painel = _tenant.Cliente(tokenPainel);
        var conexao = await _op.ConectarAsync(tokenPainel);
        _conexoes.Add(conexao);
        using var chamadas = new Caixa<SenhaChamada>(conexao, PafHubMetodos.SenhaChamada);
        using var rechamadas = new Caixa<SenhaRechamada>(conexao, PafHubMetodos.SenhaRechamada);

        var ficha = await PafOperacao.EmitirOkAsync(totem, c.Caixa, c.Normal);
        await _op.EnvelhecerFichasAsync(c.UnidadeId);
        var chamada = await PafOperacao.ChamarOkAsync(atendente);

        var aviso = await chamadas.EsperarAsync(s => s.AtendimentoId == chamada.AtendimentoId, Espera);
        aviso.ShouldNotBeNull();
        (aviso.NumeroGuiche, aviso.NomeExibicaoGuiche, aviso.NomeEstacao).ShouldBe((12, "Caixa rápido", "Mesa 12"));

        await PafOperacao.AgirOkAsync(atendente, chamada.AtendimentoId, "rechamar");
        var rechamada = await rechamadas.EsperarAsync(s => s.AtendimentoId == chamada.AtendimentoId, Espera);
        rechamada.ShouldNotBeNull();
        rechamada.NumeroGuiche.ShouldBe(12);

        var estado = await painel.GetFromJsonAsync<EstadoPainelDto>(PafOperacao.RotaEstadoPainel);
        var linha = estado!.Chamadas.ShouldHaveSingleItem();
        (linha.Senha, linha.NumeroGuiche, linha.NomeExibicaoGuiche).ShouldBe((ficha.Senha, 12, "Caixa rápido"));
    }

    [Fact]
    public async Task GuicheSemBlocoGravado_PainelRecebeNumeroNulo_ENaoFalha()
    {
        var c = await _op.CriarAsync();
        // Rows written outside this cadastro may hold any shape; a guichê block that is not an object must not break the call.
        const string foraDoFormato = """{"guiche": "texto"}""";
        await _tenant.ExecutarAsync($"""UPDATE paf."Estacoes" SET "Configuracao" = {foraDoFormato}::jsonb WHERE "Id" = {c.GuicheA}""");
        using var totem = await _op.TotemAsync(c);
        using var atendente = await _op.AtendenteAsync(c, c.GuicheA);
        using var painel = _tenant.Cliente(await _op.CredencialAsync(c.UnidadeId, c.Painel, PafPapeisEstacao.Painel));

        await PafOperacao.EmitirOkAsync(totem, c.Caixa, c.Normal);
        await _op.EnvelhecerFichasAsync(c.UnidadeId);
        await PafOperacao.ChamarOkAsync(atendente);

        var linha = (await painel.GetFromJsonAsync<EstadoPainelDto>(PafOperacao.RotaEstadoPainel))!.Chamadas.ShouldHaveSingleItem();
        (linha.NumeroGuiche, linha.NomeEstacao).ShouldBe((null, "Guichê 1"));
    }

    // ─── concorrência ────────────────────────────────────────────────

    [Fact]
    public async Task DoisUpdatesParalelos_NenhumFalha_EAsListasFicamInteirasDeUmDeles()
    {
        var c = await _op.CriarAsync();
        var a = new UpdateEstacaoCommand(c.GuicheB, "Guichê 2",
            [new EstacaoFilaItem("Guiche", c.Caixa, 0)],
            [new EstacaoPrioridadeItem(c.Normal, 0)], "1", BlocosDeTeste.Guiche(21));
        var b = new UpdateEstacaoCommand(c.GuicheB, "Guichê 2",
            [new EstacaoFilaItem("Guiche", c.Protocolo, 0), new EstacaoFilaItem("Guiche", c.Retaguarda, 1)],
            [new EstacaoPrioridadeItem(c.Preferencial, 0), new EstacaoPrioridadeItem(c.Normal, 1)], "2", BlocosDeTeste.Guiche(22));

        for (var rodada = 0; rodada < 5; rodada++)
        {
            await Task.WhenAll(Task.Run(() => EnviarAsync(c, a)), Task.Run(() => EnviarAsync(c, b)));

            var gravada = (await EstacoesAsync(c)).Single(e => e.Id == c.GuicheB);
            var deA = gravada.Ramal == "1";
            var esperada = deA ? a : b;
            gravada.Filas.Select(f => (f.Papel, f.FilaId, f.Ordem)).ShouldBe(esperada.Filas.Select(f => (f.Papel, f.FilaId, f.Ordem)));
            gravada.Prioridades.ShouldBe(esperada.Prioridades);
            gravada.Configuracao!.Guiche!.Numero.ShouldBe(deA ? 21 : 22);
        }
    }

    // ─── auxiliares ──────────────────────────────────────────────────

    private static BlocoEmissaoDto Emissao(string fundo, string fonte, string descricao) =>
        new(null, null, null, fundo, fonte, descricao, null);

    private static BlocoEmissaoDto Fonte(int px) => new(null, px, null, null, null, null, null);

    private static CreateEstacaoCommand Totem(CenarioOperacao c, string nome, BlocoEmissaoDto bloco) =>
        new(nome, "Emissao", [new EstacaoFilaItem("Emissao", c.Caixa, 0)], [], null, new BlocosEstacaoDto(bloco, null, null));

    private static CreateEstacaoCommand Painel(CenarioOperacao c, string nome, BlocoPainelDto bloco) =>
        new(nome, "Painel", [new EstacaoFilaItem("Painel", c.Caixa, 0)], [], null, new BlocosEstacaoDto(null, null, bloco));

    private static List<EstacaoFilaItem> GuicheDe(CenarioOperacao c) => [new EstacaoFilaItem("Guiche", c.Caixa, 0)];

    private Task<EstacaoAlteradaDto> AtualizarGuicheBAsync(CenarioOperacao c, bool respeitarOrdem) =>
        EnviarAsync(c, new UpdateEstacaoCommand(c.GuicheB, "Guichê 2",
            [new EstacaoFilaItem("Guiche", c.Protocolo, 0), new EstacaoFilaItem("Guiche", c.Caixa, 1)],
            [new EstacaoPrioridadeItem(c.Normal, 0), new EstacaoPrioridadeItem(c.Preferencial, 1)],
            null, new BlocosEstacaoDto(null, new BlocoGuicheDto(2, null, null, respeitarOrdem, SelecionarFicha: false), null)));

    private static async Task EncerrarAsync(HttpClient atendente, Guid atendimentoId)
    {
        await PafOperacao.AgirOkAsync(atendente, atendimentoId, "iniciar");
        await PafOperacao.AgirOkAsync(atendente, atendimentoId, "encerrar");
    }

    private async Task GravarParametroDeEmpresaAsync(Guid unidadeId, string valor)
    {
        using var scope = factory.Services.CreateScope();
        var db = _tenant.Contexto(scope, unidadeId);
        db.Parametros.Add(Parametro.CriarDeEmpresa(CatalogoParametros.FonteMinimaTotemPx.Chave, valor));
        await db.SaveChangesAsync();
    }

    private async Task<List<EstacaoDto>> EstacoesAsync(CenarioOperacao c)
    {
        using var scope = factory.Services.CreateScope();
        _tenant.Contexto(scope, c.UnidadeId);
        return [.. await scope.ServiceProvider.GetRequiredService<IMediator>().Send(new ListEstacoesQuery())];
    }

    private async Task<T> EnviarAsync<T>(CenarioOperacao c, ICommand<T> command)
    {
        using var scope = factory.Services.CreateScope();
        _tenant.Contexto(scope, c.UnidadeId);
        return await scope.ServiceProvider.GetRequiredService<IMediator>().Send(command);
    }

    private async Task<IReadOnlyList<string>> RecusaAsync<T>(CenarioOperacao c, ICommand<T> command)
    {
        var erro = await Should.ThrowAsync<CustomException>(() => EnviarAsync(c, command));
        erro.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        return erro.ErrorMessages;
    }
}
