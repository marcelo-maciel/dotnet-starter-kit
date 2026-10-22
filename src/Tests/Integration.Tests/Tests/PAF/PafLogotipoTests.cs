using FSH.Modules.PAF.Contracts.Authorization;
using FSH.Modules.PAF.Contracts.v1.Estacoes;
using FSH.Modules.PAF.Contracts.v1.Fichas;
using FSH.Modules.PAF.Contracts.v1.Midias;
using FSH.Modules.PAF.Contracts.v1.Parametros;
using FSH.Modules.PAF.Data;
using FSH.Modules.PAF.Domain;
using FSH.Modules.PAF.Features.v1.Parametros.SetParametroEmpresa;
using FSH.Modules.PAF.Parametros;
using Integration.Tests.Infrastructure;

namespace Integration.Tests.Tests.PAF;

/// <summary>
/// C19, RF06 "Logotipo" against real PostgreSQL and the real store: no row = system logo (null URL) on the totem, the
/// painel and the ficha; the empresa image reaches every unidade (a mídia of U1 downloaded from U2's totem, the explicit
/// cross-unit read); a unidade override wins until its DELETE; another unidade's or tenant's image, a video, an
/// inactive image and a non-id are 400 with nothing stored; a list admin writing the empresa level is 403.
/// </summary>
[Collection(FshCollectionDefinition.Name)]
public sealed class PafLogotipoTests(FshWebApplicationFactory factory) : IAsyncLifetime
{
    private static readonly string[] Permissoes =
    [
        PafPermissions.Parametros.View, PafPermissions.Parametros.UpdateUnidade, PafPermissions.Parametros.UpdateEmpresa,
        PafPermissions.Midias.View, PafPermissions.Midias.Create, PafPermissions.Midias.Update, PafPermissions.FichasDoDia.View,
    ];

    private static readonly string Chave = CatalogoParametros.Logotipo.Chave;
    private const string RotaConfiguracaoTotem = "/api/v1/paf/estacao/emissao/configuracao";
    private const string RotaConfiguracaoPainel = "/api/v1/paf/estacao/painel/configuracao";

    private readonly PafTenantCliente _tenant = new(factory, "paf-logo");

    public Task InitializeAsync() => _tenant.InicializarAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task SemLinha_TotemPainelEFicha_ComOLogotipoDoSistema()
    {
        var operacao = new PafOperacao(factory, _tenant);
        var cenario = await operacao.CriarAsync();
        using var totem = await operacao.TotemAsync(cenario);
        using var painel = await PainelAsync(operacao, cenario);
        using var gestor = await ListaAsync(cenario.UnidadeId);

        (await ConfiguracaoAsync(totem, RotaConfiguracaoTotem)).Logotipo.ShouldBeNull();
        (await ConfiguracaoAsync(painel, RotaConfiguracaoPainel)).Logotipo.ShouldBeNull();
        (await PafOperacao.EmitirOkAsync(totem, cenario.Caixa, cenario.Normal)).Logotipo.ShouldBeNull();
        var item = (await PafApi.OkAsync<ParametrosDaUnidadeDto>(gestor, HttpMethod.Get, "/parametros", null, null)).Itens.Single(i => i.Chave == Chave);
        (item.Tipo, item.Grupo, item.Valor, item.Origem).ShouldBe((nameof(TipoParametro.Imagem), "Logotipo", string.Empty, nameof(OrigemParametro.Fabrica)));
    }

    [Fact]
    public async Task Empresa_ImagemDeU1AlcancaU1EU2_U2Sobrescreve_DeleteVoltaAoDaEmpresa()
    {
        var operacao = new PafOperacao(factory, _tenant);
        var c1 = await operacao.CriarAsync();
        var c2 = await operacao.CriarAsync();
        using var empresa = await EmpresaAsync(c1.UnidadeId);
        using var gestorU2 = await ListaAsync(c2.UnidadeId);
        using var totemU1 = await operacao.TotemAsync(c1);
        using var totemU2 = await operacao.TotemAsync(c2);
        using var painelU2 = await PainelAsync(operacao, c2);

        var daEmpresa = await CriarImagemAsync(empresa, 300);
        daEmpresa.UnidadeId.ShouldBe(c1.UnidadeId);
        await SemConteudoAsync(empresa, HttpMethod.Put, $"/parametros/empresa/{Chave}", daEmpresa.Id.ToString().ToUpperInvariant());
        (await LinhasAsync()).ShouldHaveSingleItem().ShouldSatisfyAllConditions(
            l => l.UnidadeId.ShouldBeNull(), l => l.Valor.ShouldBe(daEmpresa.Id.ToString("D")));

        (await TamanhoDoLogotipoAsync(totemU1, RotaConfiguracaoTotem)).ShouldBe(300);
        (await TamanhoDoLogotipoAsync(totemU2, RotaConfiguracaoTotem)).ShouldBe(300, "the empresa image lives in U1 and must show in U2");
        (await TamanhoDoLogotipoAsync(painelU2, RotaConfiguracaoPainel)).ShouldBe(300);
        var fichaU2 = await PafOperacao.EmitirOkAsync(totemU2, c2.Caixa, c2.Normal);
        (await BaixarAsync(fichaU2.Logotipo.ShouldNotBeNull().Url)).ShouldBe(300);

        var daU2 = await CriarImagemAsync(gestorU2, 120);
        await SemConteudoAsync(gestorU2, HttpMethod.Put, $"/parametros/unidade/{Chave}", daU2.Id.ToString());
        (await TamanhoDoLogotipoAsync(totemU2, RotaConfiguracaoTotem)).ShouldBe(120);
        (await TamanhoDoLogotipoAsync(totemU1, RotaConfiguracaoTotem)).ShouldBe(300, "U2's override does not reach U1");
        var reimpressa = await PafApi.OkAsync<FichaEmitidaDto>(gestorU2, HttpMethod.Get, $"/fichas/{fichaU2.FichaId}/impressao", null, null);
        (await BaixarAsync(reimpressa.Logotipo.ShouldNotBeNull().Url)).ShouldBe(120, "the reprint shows the logo as it is now");

        await SemConteudoAsync(gestorU2, HttpMethod.Delete, $"/parametros/unidade/{Chave}", null);
        (await TamanhoDoLogotipoAsync(totemU2, RotaConfiguracaoTotem)).ShouldBe(300);

        // Back to the system logo: an empty value is the default, stored like any other.
        await SemConteudoAsync(empresa, HttpMethod.Put, $"/parametros/empresa/{Chave}", "");
        (await ConfiguracaoAsync(totemU2, RotaConfiguracaoTotem)).Logotipo.ShouldBeNull();
    }

    [Fact]
    public async Task ImagemDeOutraUnidadeOuTenant_Video_Inativa_NaoId_400_NadaGravado()
    {
        var u1 = await _tenant.CriarUnidadeAsync(limiteSessoes: 5);
        var u2 = await _tenant.CriarUnidadeAsync(limiteSessoes: 5);
        using var empresa = await EmpresaAsync(u1.UnidadeId);
        using var gestorU2 = await ListaAsync(u2.UnidadeId);
        var deU1 = await CriarImagemAsync(empresa, 64);
        var deU2 = await CriarImagemAsync(gestorU2, 64);
        var video = await PafMidiaUrlTests.CriarMidiaAsync(empresa, null, PafMidiaCadastroTests.Bytes(64), 10);
        var inativa = await CriarImagemAsync(empresa, 64);
        await PafMidiaCadastroTests.MidiaOkAsync(empresa, HttpMethod.Post, $"/midias/{inativa.Id}/disable", null, null);
        var api = new PafApi(factory);
        using var root = await api.RootAsync();
        var unidadeRaiz = await api.CriarUnidadeAsync(root);
        var deOutroTenant = await CriarImagemAsync(root, unidadeRaiz, 64);

        await RecusadoAsync(gestorU2, $"/parametros/unidade/{Chave}", deU1.Id.ToString(), GravacaoParametros.MensagemImagemNaoEncontrada);
        await RecusadoAsync(empresa, $"/parametros/empresa/{Chave}", deU2.Id.ToString(), GravacaoParametros.MensagemImagemNaoEncontrada);
        await RecusadoAsync(empresa, $"/parametros/empresa/{Chave}", deOutroTenant.Id.ToString(), GravacaoParametros.MensagemImagemNaoEncontrada);
        await RecusadoAsync(empresa, $"/parametros/unidade/{Chave}", video.Id.ToString(), GravacaoParametros.MensagemNaoEImagem);
        await RecusadoAsync(empresa, $"/parametros/empresa/{Chave}", inativa.Id.ToString(), GravacaoParametros.MensagemImagemInativa);
        await RecusadoAsync(empresa, $"/parametros/empresa/{Chave}", Guid.CreateVersion7().ToString(), GravacaoParametros.MensagemImagemNaoEncontrada);
        await RecusadoAsync(empresa, $"/parametros/empresa/{Chave}", "logo.png", "vazio para o padrão do sistema");
        await RecusadoAsync(empresa, $"/parametros/empresa/{Chave}", Guid.Empty.ToString(), "vazio para o padrão do sistema");

        (await LinhasAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task AdministradorDeLista_GravandoLogotipoNaEmpresa403_NaUnidadeDele204()
    {
        var u1 = await _tenant.CriarUnidadeAsync(limiteSessoes: 5);
        using var lista = await ListaAsync(u1.UnidadeId);
        var imagem = await CriarImagemAsync(lista, 64);

        using (var resposta = await EnviarAsync(lista, HttpMethod.Put, $"/parametros/empresa/{Chave}", imagem.Id.ToString()))
        {
            var corpo = await resposta.Content.ReadAsStringAsync();
            resposta.StatusCode.ShouldBe(HttpStatusCode.Forbidden, corpo);
            PafSessaoLicencaTests.Detalhe(corpo).ShouldBe(SetParametroEmpresaCommandHandler.MensagemSoAdministradorDaEmpresa);
        }

        (await LinhasAsync()).ShouldBeEmpty();
        await SemConteudoAsync(lista, HttpMethod.Put, $"/parametros/unidade/{Chave}", imagem.Id.ToString());
        (await LinhasAsync()).ShouldHaveSingleItem().UnidadeId.ShouldBe(u1.UnidadeId);
    }

    /// <summary>The parameter stores the mídia, so deactivating it after the write falls back to the system logo, never a broken image.</summary>
    [Fact]
    public async Task ImagemDesativadaDepoisDeGravada_TotemEFichaVoltamAoLogotipoDoSistema()
    {
        var operacao = new PafOperacao(factory, _tenant);
        var cenario = await operacao.CriarAsync();
        using var gestor = await ListaAsync(cenario.UnidadeId);
        using var totem = await operacao.TotemAsync(cenario);
        var imagem = await CriarImagemAsync(gestor, 80);
        await SemConteudoAsync(gestor, HttpMethod.Put, $"/parametros/unidade/{Chave}", imagem.Id.ToString());
        (await TamanhoDoLogotipoAsync(totem, RotaConfiguracaoTotem)).ShouldBe(80);

        await PafMidiaCadastroTests.MidiaOkAsync(gestor, HttpMethod.Post, $"/midias/{imagem.Id}/disable", null, null);

        (await ConfiguracaoAsync(totem, RotaConfiguracaoTotem)).Logotipo.ShouldBeNull();
        (await PafOperacao.EmitirOkAsync(totem, cenario.Caixa, cenario.Normal)).Logotipo.ShouldBeNull();
    }

    private static Task<MidiaDto> CriarImagemAsync(HttpClient cliente, int tamanho) => CriarImagemAsync(cliente, null, tamanho);

    private static async Task<MidiaDto> CriarImagemAsync(HttpClient cliente, Guid? unidade, int tamanho)
    {
        var envio = await PafMidiaCadastroTests.PedirEnvioAsync(cliente, unidade, "logo.png", "image/png");
        (await PafMidiaCadastroTests.EnviarBytesAsync(envio, PafMidiaCadastroTests.Bytes(tamanho), "image/png")).ShouldBe(HttpStatusCode.OK);
        return await PafMidiaCadastroTests.MidiaOkAsync(cliente, HttpMethod.Post, "/midias", unidade, new CreateMidiaCommand(envio.EnvioId, "Logotipo", 10));
    }

    private async Task<HttpClient> PainelAsync(PafOperacao operacao, CenarioOperacao cenario) =>
        _tenant.Cliente(await operacao.CredencialAsync(cenario.UnidadeId, cenario.Painel, PafPapeisEstacao.Painel));

    private static async Task<ConfiguracaoEstacaoDto> ConfiguracaoAsync(HttpClient estacao, string rota) =>
        (await estacao.GetFromJsonAsync<ConfiguracaoEstacaoDto>(rota)).ShouldNotBeNull();

    private static async Task<int> TamanhoDoLogotipoAsync(HttpClient estacao, string rota) =>
        await BaixarAsync((await ConfiguracaoAsync(estacao, rota)).Logotipo.ShouldNotBeNull().Url);

    private static async Task<int> BaixarAsync(Uri url)
    {
        using var http = new HttpClient { Timeout = PafMidiaCadastroTests.Prazo };
        using var resposta = await http.GetAsync(url);
        resposta.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (await resposta.Content.ReadAsByteArrayAsync()).Length;
    }

    private async Task<HttpClient> EmpresaAsync(Guid principal)
    {
        var usuario = await _tenant.CriarUsuarioEmpresaAsync(principal);
        await _tenant.DarPapelAsync(usuario.UserId, Permissoes);
        return _tenant.Cliente((await _tenant.EntrarAsync(usuario)).AccessToken);
    }

    private async Task<HttpClient> ListaAsync(params Guid[] unidades)
    {
        var usuario = await _tenant.CriarUsuarioAsync(unidades);
        await _tenant.DarPapelAsync(usuario.UserId, Permissoes);
        return _tenant.Cliente((await _tenant.EntrarAsync(usuario)).AccessToken);
    }

    private static Task<HttpResponseMessage> EnviarAsync(HttpClient cliente, HttpMethod metodo, string rota, string? valor) =>
        PafApi.EnviarAsync(cliente, metodo, rota, null, valor is null ? null : new ParametroValorRequest(valor));

    private static async Task SemConteudoAsync(HttpClient cliente, HttpMethod metodo, string rota, string? valor)
    {
        using var resposta = await EnviarAsync(cliente, metodo, rota, valor);
        resposta.StatusCode.ShouldBe(HttpStatusCode.NoContent, $"{metodo} {rota}: {await resposta.Content.ReadAsStringAsync()}");
    }

    private static async Task RecusadoAsync(HttpClient cliente, string rota, string valor, string trecho)
    {
        using var resposta = await EnviarAsync(cliente, HttpMethod.Put, rota, valor);
        var corpo = await resposta.Content.ReadAsStringAsync();
        resposta.StatusCode.ShouldBe(HttpStatusCode.BadRequest, $"{rota} {valor}: {corpo}");
        corpo.ShouldContain(trecho);
    }

    /// <summary>Logo rows across the tenant's unidades: only the unit filter is turned off.</summary>
    private async Task<List<Parametro>> LinhasAsync()
    {
        using var scope = factory.Services.CreateScope();
        var db = _tenant.Contexto(scope, unidadeCorrente: null);
        return await db.Parametros.AsNoTracking()
            .IgnoreQueryFilters([PafQueryFilters.Unidade])
            .Where(p => p.Chave == Chave)
            .ToListAsync();
    }
}
