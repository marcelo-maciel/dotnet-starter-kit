using System.Security.Cryptography;
using System.Text;
using FSH.Framework.Core.Exceptions;
using FSH.Modules.PAF.Contracts.v1.Estacoes;
using FSH.Modules.PAF.Domain;
using Integration.Tests.Infrastructure;
using Mediator;

namespace Integration.Tests.Tests.PAF;

/// <summary>
/// Credencial de estação (RF04, arquitetura seção 5): link de uso único, uma credencial por papel, política
/// por papel, renovação pelo segredo e revogação que derruba o aparelho na requisição seguinte.
/// </summary>
[Collection(FshCollectionDefinition.Name)]
public sealed class PafEstacaoTests : IAsyncLifetime
{
    private const string Resgatar = "/api/v1/paf/estacoes/vinculo/resgatar";
    private const string Renovar = "/api/v1/paf/estacoes/credencial/renovar";
    private const string RotaEmissao = "/api/v1/paf/estacao/emissao/configuracao";
    private const string RotaPainel = "/api/v1/paf/estacao/painel/configuracao";

    private readonly FshWebApplicationFactory _factory;
    private readonly PafBanco _banco;
    private readonly AuthHelper _auth;

    public PafEstacaoTests(FshWebApplicationFactory factory)
    {
        _factory = factory;
        _banco = new PafBanco(factory);
        _auth = new AuthHelper(factory);
    }

    public Task InitializeAsync() => _banco.InicializarAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Resgate_EmiteUmaCredencialPorPapel_GuardaSoHashes_ESegundoResgateFalha()
    {
        var (unidadeId, estacaoId) = await CriarEstacaoAsync(TipoEstacao.EmissaoPainel);
        var link = await GerarLinkAsync(unidadeId, estacaoId);

        using var primeiro = await ResgatarAsync(link.Token);
        primeiro.StatusCode.ShouldBe(HttpStatusCode.OK, await primeiro.Content.ReadAsStringAsync());
        var credenciais = (await primeiro.Content.ReadFromJsonAsync<CredenciaisEstacaoDto>())!.Credenciais;
        credenciais.Select(c => c.Papel).ShouldBe(["Emissao", "Painel"], ignoreOrder: true);

        using var segundo = await ResgatarAsync(link.Token);
        segundo.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        using var scope = _factory.Services.CreateScope();
        var db = _banco.Contexto(scope, unidadeId);
        var vinculo = await db.VinculosEstacao.AsNoTracking().SingleAsync(v => v.Id == link.VinculoId);
        var agoraNoBanco = await db.Database.SqlQuery<DateTimeOffset>($"SELECT now() AS \"Value\"").SingleAsync();
        vinculo.ResgatadoEm.ShouldNotBeNull();
        (agoraNoBanco - vinculo.ResgatadoEm.Value).ShouldBeLessThan(TimeSpan.FromMinutes(1));
        vinculo.LinkHash.ShouldBe(Sha256(link.Token));
        vinculo.CredencialEmissaoHash.ShouldBe(Sha256(credenciais.Single(c => c.Papel == "Emissao").Segredo));
        vinculo.CredencialPainelHash.ShouldBe(Sha256(credenciais.Single(c => c.Papel == "Painel").Segredo));
    }

    [Fact]
    public async Task Resgate_Concorrente_SoUmAparelhoVincula()
    {
        var (unidadeId, estacaoId) = await CriarEstacaoAsync(TipoEstacao.Painel);
        var link = await GerarLinkAsync(unidadeId, estacaoId);

        var respostas = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => ResgatarAsync(link.Token)));
        try
        {
            respostas.Count(r => r.StatusCode == HttpStatusCode.OK).ShouldBe(1);
            respostas.Count(r => r.StatusCode == HttpStatusCode.Unauthorized).ShouldBe(4);
        }
        finally
        {
            foreach (var r in respostas)
            {
                r.Dispose();
            }
        }
    }

    [Theory]
    [InlineData("token-que-nunca-existiu-0000000000000000000")]
    [InlineData("")]
    public async Task Resgate_LinkDesconhecidoOuVazio_Recusado(string token)
    {
        using var resposta = await ResgatarAsync(token);
        resposta.StatusCode.ShouldBeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Resgate_SemTenant_Recusado()
    {
        var (unidadeId, estacaoId) = await CriarEstacaoAsync(TipoEstacao.Emissao);
        var link = await GerarLinkAsync(unidadeId, estacaoId);

        using var client = _factory.CreateClient();
        using var resposta = await client.PostAsJsonAsync(Resgatar, new { token = link.Token });
        resposta.StatusCode.ShouldNotBe(HttpStatusCode.OK);

        // The refused attempt must not have consumed the link.
        using var valido = await ResgatarAsync(link.Token);
        valido.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Politica_CadaCredencialSoPassaNaRotaDoSeuPapel()
    {
        var (unidadeId, estacaoId) = await CriarEstacaoAsync(TipoEstacao.EmissaoPainel);
        var credenciais = await VincularAsync(unidadeId, estacaoId);
        var emissao = credenciais.Single(c => c.Papel == "Emissao").AccessToken;
        var painel = credenciais.Single(c => c.Papel == "Painel").AccessToken;

        using (var r = await GetAsync(RotaEmissao, emissao))
        {
            r.StatusCode.ShouldBe(HttpStatusCode.OK, await r.Content.ReadAsStringAsync());
            var config = await r.Content.ReadFromJsonAsync<ConfiguracaoEstacaoDto>();
            config!.EstacaoId.ShouldBe(estacaoId);
            config.UnidadeId.ShouldBe(unidadeId);
        }

        using (var r = await GetAsync(RotaPainel, painel))
        {
            r.StatusCode.ShouldBe(HttpStatusCode.OK, await r.Content.ReadAsStringAsync());
        }

        using (var r = await GetAsync(RotaEmissao, painel))
        {
            r.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }

        using (var r = await GetAsync(RotaPainel, emissao))
        {
            r.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }
    }

    [Fact]
    public async Task Politica_TokenDeUsuario_RecusadoNaRotaDeEstacao()
    {
        var root = await _auth.GetRootAdminTokenAsync();
        using var r = await GetAsync(RotaEmissao, root.AccessToken);
        r.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task CredencialDeEstacao_SemPermissaoNoBanco_RecusadaEmRotaDoKitEDeAdministracao()
    {
        var (unidadeId, estacaoId) = await CriarEstacaoAsync(TipoEstacao.Emissao);
        var token = (await VincularAsync(unidadeId, estacaoId)).Single().AccessToken;

        using (var kit = await GetAsync($"{TestConstants.IdentityBasePath}/users/search?PageNumber=1&PageSize=5", token))
        {
            // The kit permission check looks the station id up as a user and refuses (401 when the lookup fails).
            kit.StatusCode.ShouldBeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden);
        }

        using var client = ClienteComToken(token);
        using var admin = await client.PostAsync($"/api/v1/paf/estacoes/{estacaoId}/vinculo", content: null);
        admin.StatusCode.ShouldBeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Revogacao_DerrubaTokenNaRequisicaoSeguinte_ERecusaRenovacao()
    {
        var (unidadeId, estacaoId) = await CriarEstacaoAsync(TipoEstacao.EmissaoPainel);
        var credenciais = await VincularAsync(unidadeId, estacaoId);
        var emissao = credenciais.Single(c => c.Papel == "Emissao");
        var painel = credenciais.Single(c => c.Papel == "Painel");

        using (var antes = await GetAsync(RotaEmissao, emissao.AccessToken))
        {
            antes.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        await EnviarComoAdministradorAsync(unidadeId, new RevogarVinculoEstacaoCommand(estacaoId));

        using (var depois = await GetAsync(RotaEmissao, emissao.AccessToken))
        {
            depois.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }

        using (var painelDepois = await GetAsync(RotaPainel, painel.AccessToken))
        {
            painelDepois.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }

        using (var renovacao = await RenovarAsync(emissao.Segredo))
        {
            renovacao.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }

        using var scope = _factory.Services.CreateScope();
        var db = _banco.Contexto(scope, unidadeId);
        var vinculo = await db.VinculosEstacao.AsNoTracking().SingleAsync(v => v.EstacaoId == estacaoId);
        var agoraNoBanco = await db.Database.SqlQuery<DateTimeOffset>($"SELECT now() AS \"Value\"").SingleAsync();
        vinculo.RevogadoEm.ShouldNotBeNull();
        (agoraNoBanco - vinculo.RevogadoEm.Value).ShouldBeLessThan(TimeSpan.FromMinutes(1));

        // Revocation is idempotent and keeps the station.
        await EnviarComoAdministradorAsync(unidadeId, new RevogarVinculoEstacaoCommand(estacaoId));
        (await db.Estacoes.AsNoTracking().AnyAsync(e => e.Id == estacaoId)).ShouldBeTrue();
    }

    [Fact]
    public async Task Renovacao_PeloSegredo_EmiteTokenDoMesmoPapel_ESegredoDesconhecidoFalha()
    {
        var (unidadeId, estacaoId) = await CriarEstacaoAsync(TipoEstacao.Painel);
        var credencial = (await VincularAsync(unidadeId, estacaoId)).Single();

        using var renovacao = await RenovarAsync(credencial.Segredo);
        renovacao.StatusCode.ShouldBe(HttpStatusCode.OK, await renovacao.Content.ReadAsStringAsync());
        var acesso = await renovacao.Content.ReadFromJsonAsync<AcessoEstacaoDto>();
        acesso!.Papel.ShouldBe("Painel");
        acesso.AccessToken.ShouldNotBe(credencial.AccessToken);

        using (var r = await GetAsync(RotaPainel, acesso.AccessToken))
        {
            r.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        using var desconhecido = await RenovarAsync("segredo-que-nunca-existiu-000000000000000000");
        desconhecido.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        // The link secret is not a credential secret.
        var link = await GerarLinkAsync(unidadeId, estacaoId);
        using var comLink = await RenovarAsync(link.Token);
        comLink.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task EstacaoDesativada_DerrubaToken_ERecusaRenovacao()
    {
        var (unidadeId, estacaoId) = await CriarEstacaoAsync(TipoEstacao.Emissao);
        var credencial = (await VincularAsync(unidadeId, estacaoId)).Single();

        using (var scope = _factory.Services.CreateScope())
        {
            var db = _banco.Contexto(scope, unidadeId);
            await db.Database.ExecuteSqlAsync($"UPDATE paf.\"Estacoes\" SET \"Ativa\" = false WHERE \"Id\" = {estacaoId}");
        }

        using (var r = await GetAsync(RotaEmissao, credencial.AccessToken))
        {
            r.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }

        using var renovacao = await RenovarAsync(credencial.Segredo);
        renovacao.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task NovoLink_RevogaOVinculoAnterior_EOLinkAntigoNaoResgata()
    {
        var (unidadeId, estacaoId) = await CriarEstacaoAsync(TipoEstacao.Emissao);
        var antigo = (await VincularAsync(unidadeId, estacaoId)).Single();
        var linkNaoUsado = await GerarLinkAsync(unidadeId, estacaoId);
        var linkNovo = await GerarLinkAsync(unidadeId, estacaoId);

        using (var r = await GetAsync(RotaEmissao, antigo.AccessToken))
        {
            r.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }

        using (var r = await ResgatarAsync(linkNaoUsado.Token))
        {
            r.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }

        using (var r = await ResgatarAsync(linkNovo.Token))
        {
            r.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        using var scope = _factory.Services.CreateScope();
        var db = _banco.Contexto(scope, unidadeId);
        (await db.VinculosEstacao.CountAsync(v => v.EstacaoId == estacaoId && v.RevogadoEm == null)).ShouldBe(1);
    }

    [Theory]
    [InlineData(TipoEstacao.Guiche)]
    [InlineData(TipoEstacao.Administracao)]
    public async Task GerarLink_EstacaoSemPapelDeCredencial_Recusado(TipoEstacao tipo)
    {
        var (unidadeId, estacaoId) = await CriarEstacaoAsync(tipo);
        var erro = await Should.ThrowAsync<CustomException>(() => GerarLinkAsync(unidadeId, estacaoId));
        erro.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task GerarLink_EstacaoDeOutraUnidade_NaoEncontrada()
    {
        var (_, estacaoId) = await CriarEstacaoAsync(TipoEstacao.Emissao);
        var (outraUnidade, _) = await CriarEstacaoAsync(TipoEstacao.Emissao);
        await Should.ThrowAsync<NotFoundException>(() => GerarLinkAsync(outraUnidade, estacaoId));
    }

    [Fact]
    public async Task GerarLink_PeloHttp_SemUnidadeCorrente_FalhaFechado()
    {
        // Until the session slice resolves a person's current unit, the admin route sees no station at all.
        var (_, estacaoId) = await CriarEstacaoAsync(TipoEstacao.Emissao);
        using var client = await _auth.CreateRootAdminClientAsync();
        using var r = await client.PostAsync($"/api/v1/paf/estacoes/{estacaoId}/vinculo", content: null);
        r.StatusCode.ShouldBe(HttpStatusCode.NotFound, await r.Content.ReadAsStringAsync());
    }

    [Fact]
    public void InstanteDaRevogacao_EhTraduzidoParaORelogioDoBanco()
    {
        using var scope = _factory.Services.CreateScope();
        var db = _banco.Contexto(scope, Guid.NewGuid());
        db.VinculosEstacao.Select(v => DateTimeOffset.UtcNow).ToQueryString().ShouldContain("now()");
    }

    // ─── helpers ────────────────────────────────────────────────────────

    private static string Sha256(string valor) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(valor)));

    private async Task<(Guid UnidadeId, Guid EstacaoId)> CriarEstacaoAsync(TipoEstacao tipo)
    {
        var cadastro = await _banco.CriarCadastroAsync();
        using var scope = _factory.Services.CreateScope();
        var db = _banco.Contexto(scope, unidadeCorrente: null);
        var estacao = Estacao.Criar(cadastro.UnidadeId, $"Estação {tipo}", tipo);
        db.Estacoes.Add(estacao);
        await db.SaveChangesAsync();
        return (cadastro.UnidadeId, estacao.Id);
    }

    // The handler runs for real against PostgreSQL; only the current unit is set by hand, because resolving
    // it for a person is the session slice, not this one.
    private async Task<TResposta> EnviarComoAdministradorAsync<TResposta>(Guid unidadeId, ICommand<TResposta> comando)
    {
        using var scope = _factory.Services.CreateScope();
        _banco.Contexto(scope, unidadeId);
        return await scope.ServiceProvider.GetRequiredService<IMediator>().Send(comando);
    }

    private Task<VinculoEstacaoGeradoDto> GerarLinkAsync(Guid unidadeId, Guid estacaoId) =>
        EnviarComoAdministradorAsync(unidadeId, new GerarVinculoEstacaoCommand(estacaoId));


    private async Task<IReadOnlyList<CredencialEstacaoDto>> VincularAsync(Guid unidadeId, Guid estacaoId)
    {
        var link = await GerarLinkAsync(unidadeId, estacaoId);
        using var resposta = await ResgatarAsync(link.Token);
        resposta.StatusCode.ShouldBe(HttpStatusCode.OK, await resposta.Content.ReadAsStringAsync());
        return (await resposta.Content.ReadFromJsonAsync<CredenciaisEstacaoDto>())!.Credenciais;
    }

    private async Task<HttpResponseMessage> ResgatarAsync(string token)
    {
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("tenant", TestConstants.RootTenantId);
        return await client.PostAsJsonAsync(Resgatar, new { token });
    }

    private async Task<HttpResponseMessage> RenovarAsync(string segredo)
    {
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("tenant", TestConstants.RootTenantId);
        return await client.PostAsJsonAsync(Renovar, new { segredo });
    }

    private HttpClient ClienteComToken(string token)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        client.DefaultRequestHeaders.Add("tenant", TestConstants.RootTenantId);
        return client;
    }

    private async Task<HttpResponseMessage> GetAsync(string rota, string token)
    {
        using var client = ClienteComToken(token);
        return await client.GetAsync(rota);
    }
}
