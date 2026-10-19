using FSH.Modules.PAF.Contracts.Authorization;
using FSH.Modules.PAF.Contracts.v1.Estacoes;
using FSH.Modules.PAF.Domain;
using Integration.Tests.Infrastructure;

namespace Integration.Tests.Tests.PAF;

/// <summary>
/// What the station cadastro screen needs to generate and revoke a device link (RF04, C06): the listing shows the live
/// link, whether and for which roles it was redeemed, and the last connection per role; the person routes answer 403
/// without the link permissions.
/// </summary>
[Collection(FshCollectionDefinition.Name)]
public sealed class PafVinculoEstacaoTelaTests(FshWebApplicationFactory factory) : IAsyncLifetime
{
    private const string RotaListar = "/api/v1/paf/estacoes";
    private const string RotaResgatar = "/api/v1/paf/estacoes/vinculo/resgatar";
    private const string RotaConfiguracaoPainel = "/api/v1/paf/estacao/painel/configuracao";

    private readonly PafTenantCliente _tenant = new(factory, "paf-c06");
    private PafOperacao _op = default!;

    public async Task InitializeAsync()
    {
        await _tenant.InicializarAsync();
        _op = new PafOperacao(factory, _tenant);
    }

    public Task DisposeAsync() => _op.DrenarCaixaDeSaidaAsync();

    [Fact]
    public async Task Listagem_AcompanhaGerarResgatarGerarDeNovoERevogar()
    {
        var c = await _op.CriarAsync();
        var combinada = await CriarEstacaoAsync(c.UnidadeId, TipoEstacao.EmissaoPainel);
        using var gerente = await PessoaAsync(c.UnidadeId,
            PafPermissions.Estacoes.View, PafPermissions.VinculosEstacao.Generate, PafPermissions.VinculosEstacao.Revoke);

        (await EstacaoAsync(gerente, combinada)).VinculoAtual.ShouldBeNull("estação nova não tem link");

        var primeiro = await GerarAsync(gerente, combinada);
        primeiro.Tenant.ShouldBe(_tenant.TenantId, "a tela monta o link com o cliente que o servidor devolve");
        var gerado = (await EstacaoAsync(gerente, combinada)).VinculoAtual.ShouldNotBeNull();
        gerado.ResgatadoEm.ShouldBeNull();
        gerado.PapeisResgatados.ShouldBeEmpty();

        var credenciais = await ResgatarOkAsync(primeiro.Token);
        var resgatado = (await EstacaoAsync(gerente, combinada)).VinculoAtual.ShouldNotBeNull();
        resgatado.CriadoEm.ShouldBe(gerado.CriadoEm);
        var resgatadoEm = resgatado.ResgatadoEm.ShouldNotBeNull();
        (resgatadoEm - resgatado.CriadoEm).ShouldBeGreaterThanOrEqualTo(TimeSpan.Zero);
        resgatado.PapeisResgatados.ShouldBe([PafPapeisEstacao.Emissao, PafPapeisEstacao.Painel], ignoreOrder: true);

        // Only O13 writes these columns; here the instant is set by hand to prove each one reaches its own field.
        await _tenant.ExecutarAsync($"UPDATE paf.\"Estacoes\" SET \"UltimaConexaoPainelEm\" = now() - interval '5 minutes' WHERE \"Id\" = {combinada}");
        var comConexao = await EstacaoAsync(gerente, combinada);
        comConexao.UltimaConexaoEmissaoEm.ShouldBeNull("a conexão do painel não toca a da emissão");
        comConexao.UltimaConexaoPainelEm.ShouldNotBeNull();

        var segundo = await GerarAsync(gerente, combinada);
        var regerado = (await EstacaoAsync(gerente, combinada)).VinculoAtual.ShouldNotBeNull();
        regerado.CriadoEm.ShouldBeGreaterThan(gerado.CriadoEm);
        regerado.ResgatadoEm.ShouldBeNull("o link novo espera o seu aparelho");
        using (var antigo = await ResgatarAsync(primeiro.Token))
        {
            antigo.StatusCode.ShouldBe(HttpStatusCode.Unauthorized, "o primeiro link caiu com a nova geração");
        }

        using (var painelAntigo = _tenant.Cliente(credenciais.Single(x => x.Papel == PafPapeisEstacao.Painel).AccessToken))
        using (var recusa = await painelAntigo.GetAsync(RotaConfiguracaoPainel))
        {
            recusa.StatusCode.ShouldBe(HttpStatusCode.Unauthorized, "o aparelho do link anterior caiu");
        }

        var novas = await ResgatarOkAsync(segundo.Token);
        using (var revogar = await gerente.PostAsync($"/api/v1/paf/estacoes/{combinada}/vinculo/revogar", content: null))
        {
            revogar.StatusCode.ShouldBe(HttpStatusCode.NoContent, await revogar.Content.ReadAsStringAsync());
        }

        (await EstacaoAsync(gerente, combinada)).VinculoAtual.ShouldBeNull("revogado não é link atual");
        using var painel = _tenant.Cliente(novas.Single(x => x.Papel == PafPapeisEstacao.Painel).AccessToken);
        using var derrubado = await painel.GetAsync(RotaConfiguracaoPainel);
        derrubado.StatusCode.ShouldBe(HttpStatusCode.Unauthorized, "revogar derruba o painel");
    }

    [Fact]
    public async Task Listagem_SoPainelResgatado_ListaSoOPapelDoTipo_EGuicheSemLink()
    {
        var c = await _op.CriarAsync();
        using var gerente = await PessoaAsync(c.UnidadeId, PafPermissions.Estacoes.View, PafPermissions.VinculosEstacao.Generate);

        var link = await GerarAsync(gerente, c.Painel);
        await ResgatarOkAsync(link.Token);
        (await EstacaoAsync(gerente, c.Painel)).VinculoAtual.ShouldNotBeNull().PapeisResgatados.ShouldBe([PafPapeisEstacao.Painel]);

        using var guiche = await gerente.PostAsync($"/api/v1/paf/estacoes/{c.GuicheA}/vinculo", content: null);
        guiche.StatusCode.ShouldBe(HttpStatusCode.BadRequest, "guichê exige login de pessoa, não tem link");
        (await EstacaoAsync(gerente, c.GuicheA)).VinculoAtual.ShouldBeNull();
    }

    [Fact]
    public async Task SemPermissaoDeVinculo_GerarERevogar403_EListagemContinua()
    {
        var c = await _op.CriarAsync();
        using var leitor = await PessoaAsync(c.UnidadeId, PafPermissions.Estacoes.View);

        using (var gerar = await leitor.PostAsync($"/api/v1/paf/estacoes/{c.Totem}/vinculo", content: null))
        {
            gerar.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }

        using (var revogar = await leitor.PostAsync($"/api/v1/paf/estacoes/{c.Totem}/vinculo/revogar", content: null))
        {
            revogar.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }

        (await EstacaoAsync(leitor, c.Totem)).VinculoAtual.ShouldBeNull("a recusa não gerou link");

        // Generate and revoke are separate permissions: the screen shows each button by its own one.
        using var soGera = await PessoaAsync(c.UnidadeId, PafPermissions.Estacoes.View, PafPermissions.VinculosEstacao.Generate);
        await GerarAsync(soGera, c.Totem);
        using var revogarSemPermissao = await soGera.PostAsync($"/api/v1/paf/estacoes/{c.Totem}/vinculo/revogar", content: null);
        revogarSemPermissao.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await EstacaoAsync(soGera, c.Totem)).VinculoAtual.ShouldNotBeNull();
    }

    private async Task<HttpClient> PessoaAsync(Guid unidadeId, params string[] permissoes)
    {
        var usuario = await _tenant.CriarUsuarioAsync(unidadeId);
        await _tenant.DarPapelAsync(usuario.UserId, permissoes);
        return _tenant.Cliente((await _tenant.EntrarAsync(usuario)).AccessToken);
    }

    private async Task<Guid> CriarEstacaoAsync(Guid unidadeId, TipoEstacao tipo)
    {
        using var scope = factory.Services.CreateScope();
        var db = _tenant.Contexto(scope, unidadeCorrente: null);
        var estacao = Estacao.Criar(unidadeId, $"Estação {tipo}", tipo);
        db.Estacoes.Add(estacao);
        await db.SaveChangesAsync();
        return estacao.Id;
    }

    private static async Task<EstacaoDto> EstacaoAsync(HttpClient cliente, Guid estacaoId)
    {
        using var resposta = await cliente.GetAsync(RotaListar);
        resposta.StatusCode.ShouldBe(HttpStatusCode.OK, await resposta.Content.ReadAsStringAsync());
        var estacoes = await resposta.Content.ReadFromJsonAsync<List<EstacaoDto>>();
        return estacoes!.Single(e => e.Id == estacaoId);
    }

    private static async Task<VinculoEstacaoGeradoDto> GerarAsync(HttpClient cliente, Guid estacaoId)
    {
        using var resposta = await cliente.PostAsync($"/api/v1/paf/estacoes/{estacaoId}/vinculo", content: null);
        resposta.StatusCode.ShouldBe(HttpStatusCode.OK, await resposta.Content.ReadAsStringAsync());
        return (await resposta.Content.ReadFromJsonAsync<VinculoEstacaoGeradoDto>())!;
    }

    private async Task<HttpResponseMessage> ResgatarAsync(string token)
    {
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("tenant", _tenant.TenantId);
        return await client.PostAsJsonAsync(RotaResgatar, new { token });
    }

    private async Task<IReadOnlyList<CredencialEstacaoDto>> ResgatarOkAsync(string token)
    {
        using var resposta = await ResgatarAsync(token);
        resposta.StatusCode.ShouldBe(HttpStatusCode.OK, await resposta.Content.ReadAsStringAsync());
        return (await resposta.Content.ReadFromJsonAsync<CredenciaisEstacaoDto>())!.Credenciais;
    }
}
