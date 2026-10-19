using Integration.Tests.Infrastructure;

namespace Integration.Tests.Tests.PAF;

/// <summary>
/// Conferência de tenant do middleware do PAF (arquitetura seção 5, correção de 17/10/2026): o kit resolve a
/// requisição autenticada pela claim e ignora em silêncio um cabeçalho diferente; o PAF recusa com 403, numa
/// rota do PAF e numa do kit, exceto para o operador do tenant raiz, cuja sobreposição o kit trata.
/// </summary>
[Collection(FshCollectionDefinition.Name)]
public sealed class PafConferenciaTenantTests : IAsyncLifetime
{
    private const string RotaKit = $"{TestConstants.IdentityBasePath}/users/search?PageNumber=1&PageSize=50";
    private const string Renovar = "/api/v1/paf/estacoes/credencial/renovar";
    private const string MensagemDaRecusa = "does not match the authenticated tenant";

    private readonly FshWebApplicationFactory _factory;
    private readonly AuthHelper _auth;

    private string _tenantA = default!;
    private string _tenantB = default!;
    private string _adminA = default!;

    public PafConferenciaTenantTests(FshWebApplicationFactory factory)
    {
        _factory = factory;
        _auth = new AuthHelper(factory);
    }

    public async Task InitializeAsync()
    {
        var unico = Guid.NewGuid().ToString("N")[..8];
        _tenantA = $"pafct-a-{unico}";
        _tenantB = $"pafct-b-{unico}";
        _adminA = $"admin-a-{unico}@pafct.com";

        using var root = await _auth.CreateRootAdminClientAsync();
        await CriarTenantAsync(root, _tenantA, _adminA);
        await CriarTenantAsync(root, _tenantB, $"admin-b-{unico}@pafct.com");
        await TenantProvisioningWait.WaitForProvisioningAsync(root, _tenantA);
        await TenantProvisioningWait.WaitForProvisioningAsync(root, _tenantB);
        await PafTenantCliente.PrepararAdministradorAsync(_factory, _tenantA, PafTenantCliente.LimiteDaUnidadeInicial);
        _ = await TokenComRetentativaAsync(_adminA, _tenantA);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task TokenDoTenantA_ComCabecalhoDoTenantB_RecusadoNaRotaDoKit()
    {
        using var r = await GetAsync(RotaKit, await TokenComRetentativaAsync(_adminA, _tenantA), _tenantB);
        r.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await r.Content.ReadAsStringAsync()).ShouldContain(MensagemDaRecusa);
    }

    [Fact]
    public async Task TokenDoTenantA_ComCabecalhoDoTenantB_RecusadoNaRotaDoPaf()
    {
        var token = await TokenComRetentativaAsync(_adminA, _tenantA);

        using var mesmo = await PostAsync($"/api/v1/paf/estacoes/{Guid.NewGuid()}/vinculo", token, _tenantA);
        // Passes the conference and the permission; no current unit yet, so the unit filter finds nothing.
        mesmo.StatusCode.ShouldBe(HttpStatusCode.NotFound, await mesmo.Content.ReadAsStringAsync());

        using var outro = await PostAsync($"/api/v1/paf/estacoes/{Guid.NewGuid()}/vinculo", token, _tenantB);
        outro.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await outro.Content.ReadAsStringAsync()).ShouldContain(MensagemDaRecusa);
    }

    [Fact]
    public async Task TokenDoTenantA_ComQueryDoTenantB_Recusado()
    {
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", await TokenComRetentativaAsync(_adminA, _tenantA));
        using var r = await client.GetAsync($"{RotaKit}&tenant={_tenantB}");
        r.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await r.Content.ReadAsStringAsync()).ShouldContain(MensagemDaRecusa);
    }

    [Fact]
    public async Task TokenDoTenantA_ComCabecalhoDoProprioTenant_Passa()
    {
        using var r = await GetAsync(RotaKit, await TokenComRetentativaAsync(_adminA, _tenantA), _tenantA);
        r.StatusCode.ShouldBe(HttpStatusCode.OK, await r.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task TokenDoTenantA_SemCabecalho_Passa()
    {
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", await TokenComRetentativaAsync(_adminA, _tenantA));
        using var r = await client.GetAsync(RotaKit);
        r.StatusCode.ShouldBe(HttpStatusCode.OK, await r.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task OperadorRaiz_ComCabecalhoDeOutroTenant_SobreposicaoDoKitContinua()
    {
        var root = await _auth.GetRootAdminTokenAsync();
        using var r = await GetAsync(RotaKit, root.AccessToken, _tenantA);
        r.StatusCode.ShouldBe(HttpStatusCode.OK, await r.Content.ReadAsStringAsync());
        (await r.Content.ReadAsStringAsync()).ShouldContain(_adminA);
    }

    [Fact]
    public async Task LoginAnonimo_ComCabecalho_NaoEhAfetado()
    {
        var token = await _auth.GetTokenAsync(_adminA, TestConstants.DefaultPassword, _tenantA);
        token.AccessToken.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task RenovacaoDeEstacao_ComSegredoDeOutroTenant_Recusada()
    {
        // Anonymous renewal resolves the tenant by header; a secret only exists inside its own tenant's rows.
        var banco = new PafBanco(_factory);
        await banco.InicializarAsync();
        var cadastro = await banco.CriarCadastroAsync();
        Guid estacaoId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = banco.Contexto(scope, unidadeCorrente: null);
            var estacao = FSH.Modules.PAF.Domain.Estacao.Criar(cadastro.UnidadeId, "Totem", FSH.Modules.PAF.Domain.TipoEstacao.Emissao);
            db.Estacoes.Add(estacao);
            await db.SaveChangesAsync();
            estacaoId = estacao.Id;
        }

        string token;
        using (var scope = _factory.Services.CreateScope())
        {
            banco.Contexto(scope, cadastro.UnidadeId);
            var link = await scope.ServiceProvider.GetRequiredService<Mediator.IMediator>()
                .Send(new FSH.Modules.PAF.Contracts.v1.Estacoes.GerarVinculoEstacaoCommand(estacaoId));
            token = link.Token;
        }

        using var anonimoRoot = _factory.CreateClient();
        anonimoRoot.DefaultRequestHeaders.Add("tenant", TestConstants.RootTenantId);
        using var resgate = await anonimoRoot.PostAsJsonAsync("/api/v1/paf/estacoes/vinculo/resgatar", new { token });
        resgate.StatusCode.ShouldBe(HttpStatusCode.OK);
        var credencial = (await resgate.Content
            .ReadFromJsonAsync<FSH.Modules.PAF.Contracts.v1.Estacoes.CredenciaisEstacaoDto>())!.Credenciais.Single();

        using var anonimoA = _factory.CreateClient();
        anonimoA.DefaultRequestHeaders.Add("tenant", _tenantA);
        using var renovacao = await anonimoA.PostAsJsonAsync(Renovar, new { segredo = credencial.Segredo });
        renovacao.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        // A station credential is never exempt, even when its tenant is root.
        using var comToken = _factory.CreateClient();
        comToken.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", credencial.AccessToken);
        comToken.DefaultRequestHeaders.Add("tenant", _tenantA);
        using var r = await comToken.GetAsync("/api/v1/paf/estacao/emissao/configuracao");
        r.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await r.Content.ReadAsStringAsync()).ShouldContain(MensagemDaRecusa);
    }

    // ─── helpers ────────────────────────────────────────────────────────

    private async Task<HttpResponseMessage> GetAsync(string rota, string token, string tenant)
    {
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        client.DefaultRequestHeaders.Add("tenant", tenant);
        return await client.GetAsync(rota);
    }

    private async Task<HttpResponseMessage> PostAsync(string rota, string token, string tenant)
    {
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        client.DefaultRequestHeaders.Add("tenant", tenant);
        return await client.PostAsync(rota, content: null);
    }

    // Provisioning can report "Completed" a tick before the seeded admin can log in (same race as
    // TenantHeaderOverrideTests); a bounded retry waits for it.
    private async Task<string> TokenComRetentativaAsync(string email, string tenant)
    {
        Exception? ultimo = null;
        for (var i = 0; i < 30; i++)
        {
            try
            {
                return (await _auth.GetTokenAsync(email, TestConstants.DefaultPassword, tenant)).AccessToken;
            }
            catch (HttpRequestException ex)
            {
                ultimo = ex;
                await Task.Delay(500);
            }
        }

        throw ultimo ?? new InvalidOperationException("token issuance failed");
    }

    private static async Task CriarTenantAsync(HttpClient root, string tenantId, string adminEmail)
    {
        using var response = await root.PostAsJsonAsync(TestConstants.TenantsBasePath, new
        {
            id = tenantId,
            name = $"PAFCT {tenantId}",
            connectionString = (string?)null,
            adminEmail,
            adminPassword = TestConstants.DefaultPassword,
            issuer = $"{tenantId}.issuer",
        });
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
    }
}
