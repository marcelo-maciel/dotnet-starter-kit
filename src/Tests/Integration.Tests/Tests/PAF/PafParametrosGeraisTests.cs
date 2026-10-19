using FSH.Modules.PAF.Contracts.Authorization;
using FSH.Modules.PAF.Contracts.v1.Atendimentos;
using FSH.Modules.PAF.Contracts.v1.Estacoes;
using FSH.Modules.PAF.Contracts.v1.Parametros;
using FSH.Modules.PAF.Parametros;
using Integration.Tests.Infrastructure;

namespace Integration.Tests.Tests.PAF;

/// <summary>
/// C20, RF06 Geral (requisitos l.228): the call message reaches the painel's configuration and its DELETE brings the
/// default back; the voice cannot be switched off (D29, RNF19); the guichê's waiting counts are the total, or per fila of
/// the station when "exibir informação detalhada de fila" is on (D30), counted with the call's own predicate.
/// </summary>
[Collection(FshCollectionDefinition.Name)]
public sealed class PafParametrosGeraisTests(FshWebApplicationFactory factory) : IAsyncLifetime
{
    private const string RotaAguardando = "/api/v1/paf/guiche/aguardando";
    private const string RotaConfiguracaoPainel = "/api/v1/paf/estacao/painel/configuracao";

    private static readonly string[] PermissoesDeParametro = [PafPermissions.Parametros.View, PafPermissions.Parametros.UpdateUnidade];
    private static readonly string Mensagem = CatalogoParametros.MensagemAoChamar.Chave;
    private static readonly string Voz = CatalogoParametros.ChamarFichaPorVoz.Chave;
    private static readonly string Detalhada = CatalogoParametros.ExibirInformacaoDetalhadaFila.Chave;

    private readonly PafTenantCliente _tenant = new(factory, "paf-ger");
    private PafOperacao _op = default!;

    public async Task InitializeAsync()
    {
        await _tenant.InicializarAsync();
        _op = new PafOperacao(factory, _tenant);
    }

    public Task DisposeAsync() => _op.DrenarCaixaDeSaidaAsync();

    // ─── mensagem ao chamar e voz ────────────────────────────────────

    [Fact]
    public async Task Mensagem_GravadaChegaAoPainel_DeleteVoltaAoPadrao_ETotemNaoRecebe()
    {
        var c = await _op.CriarAsync();
        using var gestor = await GestorAsync(c.UnidadeId);
        using var painel = _tenant.Cliente(await _op.CredencialAsync(c.UnidadeId, c.Painel, PafPapeisEstacao.Painel));
        using var totem = await _op.TotemAsync(c);

        (await AnuncioAsync(painel)).ShouldBe(new AnuncioDaChamadaDto(string.Empty, CatalogoParametros.FalaSoSenha));

        await SemConteudoAsync(gestor, HttpMethod.Put, $"/parametros/unidade/{Mensagem}", "  Dirija-se ao guichê indicado  ");
        await SemConteudoAsync(gestor, HttpMethod.Put, $"/parametros/unidade/{Voz}", CatalogoParametros.FalaSenhaEMensagem);
        (await AnuncioAsync(painel)).ShouldBe(new AnuncioDaChamadaDto("Dirija-se ao guichê indicado", CatalogoParametros.FalaSenhaEMensagem));

        await SemConteudoAsync(gestor, HttpMethod.Delete, $"/parametros/unidade/{Mensagem}", null);
        await SemConteudoAsync(gestor, HttpMethod.Delete, $"/parametros/unidade/{Voz}", null);
        (await AnuncioAsync(painel)).ShouldBe(new AnuncioDaChamadaDto(string.Empty, CatalogoParametros.FalaSoSenha));

        var doTotem = (await totem.GetFromJsonAsync<ConfiguracaoEstacaoDto>("/api/v1/paf/estacao/emissao/configuracao"))!;
        doTotem.AnuncioDaChamada.ShouldBeNull("só o painel anuncia a chamada");
    }

    [Fact]
    public async Task Mensagem_AcimaDoMaximoOuComQuebra_400_ENadaGravado()
    {
        var c = await _op.CriarAsync();
        using var gestor = await GestorAsync(c.UnidadeId);
        var maxima = new string('x', CatalogoParametros.TamanhoMaximoMensagemAoChamar);

        await RecusadoAsync(gestor, Mensagem, maxima + "x");
        await RecusadoAsync(gestor, Mensagem, "linha 1\nlinha 2");
        (await LinhasAsync(c.UnidadeId, Mensagem)).ShouldBeEmpty();

        await SemConteudoAsync(gestor, HttpMethod.Put, $"/parametros/unidade/{Mensagem}", maxima);
        (await LinhasAsync(c.UnidadeId, Mensagem)).ShouldBe([maxima]);
    }

    [Theory]
    [InlineData("false")]
    [InlineData("true")]
    [InlineData("0")]
    [InlineData("")]
    public async Task Voz_TentativaDeDesligar_400_EAVozContinuaFalandoASenha(string valor)
    {
        var c = await _op.CriarAsync();
        using var gestor = await GestorAsync(c.UnidadeId);
        using var painel = _tenant.Cliente(await _op.CredencialAsync(c.UnidadeId, c.Painel, PafPapeisEstacao.Painel));

        await RecusadoAsync(gestor, Voz, valor);

        (await LinhasAsync(c.UnidadeId, Voz)).ShouldBeEmpty();
        (await AnuncioAsync(painel)).ChamarFichaPorVoz.ShouldBe(CatalogoParametros.FalaSoSenha);
    }

    // ─── informação detalhada de fila ────────────────────────────────

    [Fact]
    public async Task Aguardando_Desligado_SoOTotal_SemDetalhePorFila()
    {
        var c = await _op.CriarAsync();
        using var totem = await _op.TotemAsync(c);
        using var guicheB = await _op.AtendenteAsync(c, c.GuicheB);
        await PafOperacao.EmitirOkAsync(totem, c.Caixa, c.Normal);
        await PafOperacao.EmitirOkAsync(totem, c.Caixa, c.Preferencial);
        await PafOperacao.EmitirOkAsync(totem, c.Protocolo, c.Normal);
        await _op.EnvelhecerFichasAsync(c.UnidadeId);

        var aguardando = await AguardandoAsync(guicheB);

        aguardando.Total.ShouldBe(3);
        aguardando.PorFila.ShouldBeNull("desligado, a tela do guichê mostra só o total (D30)");
    }

    [Fact]
    public async Task Aguardando_Ligado_PorFilaDaEstacao_ComZero_SoOQueOGuichePodeChamar()
    {
        var c = await _op.CriarAsync();
        using var gestor = await GestorAsync(c.UnidadeId);
        using var totem = await _op.TotemAsync(c);
        using var guicheA = await _op.AtendenteAsync(c, c.GuicheA);
        using var guicheB = await _op.AtendenteAsync(c, c.GuicheB);
        await SemConteudoAsync(gestor, HttpMethod.Put, $"/parametros/unidade/{Detalhada}", "true");

        await PafOperacao.EmitirOkAsync(totem, c.Caixa, c.Normal);
        await PafOperacao.EmitirOkAsync(totem, c.Caixa, c.Preferencial);
        var ontem = await PafOperacao.EmitirOkAsync(totem, c.Caixa, c.Normal);
        await _op.EnvelhecerFichasAsync(c.UnidadeId);
        await ElegivelOntemAsync(ontem.AtendimentoId);
        // Younger than the five-second guard: the call cannot reach it yet, so it is not counted yet either.
        await PafOperacao.EmitirOkAsync(totem, c.Caixa, c.Normal);

        var deB = await AguardandoAsync(guicheB);
        deB.Total.ShouldBe(2, "a ficha de ontem (somente fichas da data atual) e a de menos de 5 s ficam fora");
        deB.PorFila.ShouldNotBeNull().Select(f => (f.Fila, f.Quantidade))
            .ShouldBe([("Protocolo", 0), ("Caixa", 2)], "ordem da estação, fila sem ficha incluída");

        var deA = await AguardandoAsync(guicheA);
        deA.PorFila.ShouldNotBeNull().Select(f => f.FilaId).ShouldBe([c.Caixa], "só as filas do papel guichê da estação");

        // The count follows the call: what guichê B calls leaves the count of guichê A too.
        var chamada = await PafOperacao.ChamarOkAsync(guicheB);
        chamada.FilaId.ShouldBe(c.Caixa);
        (await AguardandoAsync(guicheA)).Total.ShouldBe(1);

        await SemConteudoAsync(gestor, HttpMethod.Put, $"/parametros/unidade/{CatalogoParametros.OrdenacaoFilas.Chave}", CatalogoParametros.OrdenacaoPorNome);
        (await AguardandoAsync(guicheB)).PorFila.ShouldNotBeNull().Select(f => f.Fila)
            .ShouldBe(["Caixa", "Protocolo"], "ordenação de filas por nome");
    }

    [Fact]
    public async Task Aguardando_SemGuicheSelecionado_409()
    {
        var c = await _op.CriarAsync();
        var usuario = await _tenant.CriarUsuarioAsync(c.UnidadeId);
        using var semGuiche = _tenant.Cliente((await _tenant.EntrarAsync(usuario)).AccessToken);

        using var resposta = await semGuiche.GetAsync(RotaAguardando);

        resposta.StatusCode.ShouldBe(HttpStatusCode.Conflict, await resposta.Content.ReadAsStringAsync());
    }

    // ─── apoio ───────────────────────────────────────────────────────

    private async Task<HttpClient> GestorAsync(Guid unidadeId)
    {
        var usuario = await _tenant.CriarUsuarioAsync(unidadeId);
        await _tenant.DarPapelAsync(usuario.UserId, PermissoesDeParametro);
        return _tenant.Cliente((await _tenant.EntrarAsync(usuario)).AccessToken);
    }

    private Task<int> ElegivelOntemAsync(Guid atendimentoId) =>
        _tenant.ExecutarAsync($"""UPDATE paf."Atendimentos" SET "ElegivelDesde" = "ElegivelDesde" - interval '1 day' WHERE "Id" = {atendimentoId}""");

    private static async Task<AnuncioDaChamadaDto> AnuncioAsync(HttpClient painel)
    {
        var configuracao = (await painel.GetFromJsonAsync<ConfiguracaoEstacaoDto>(RotaConfiguracaoPainel))!;
        return configuracao.AnuncioDaChamada.ShouldNotBeNull();
    }

    private static async Task<AguardandoGuicheDto> AguardandoAsync(HttpClient guiche)
    {
        using var resposta = await guiche.GetAsync(RotaAguardando);
        resposta.StatusCode.ShouldBe(HttpStatusCode.OK, await resposta.Content.ReadAsStringAsync());
        return (await resposta.Content.ReadFromJsonAsync<AguardandoGuicheDto>())!;
    }

    private static async Task SemConteudoAsync(HttpClient cliente, HttpMethod metodo, string rota, string? valor)
    {
        using var resposta = await PafApi.EnviarAsync(cliente, metodo, rota, null, valor is null ? null : new ParametroValorRequest(valor));
        resposta.StatusCode.ShouldBe(HttpStatusCode.NoContent, $"{metodo} {rota}: {await resposta.Content.ReadAsStringAsync()}");
    }

    private static async Task RecusadoAsync(HttpClient cliente, string chave, string valor)
    {
        using var resposta = await PafApi.EnviarAsync(cliente, HttpMethod.Put, $"/parametros/unidade/{chave}", null, new ParametroValorRequest(valor));
        resposta.StatusCode.ShouldBe(HttpStatusCode.BadRequest, $"{chave}={valor}: {await resposta.Content.ReadAsStringAsync()}");
    }

    private async Task<List<string>> LinhasAsync(Guid unidadeId, string chave)
    {
        using var scope = factory.Services.CreateScope();
        var db = _tenant.Contexto(scope, unidadeId);
        return await db.Parametros.AsNoTracking().Where(p => p.Chave == chave && p.UnidadeId == unidadeId).Select(p => p.Valor).ToListAsync();
    }
}
