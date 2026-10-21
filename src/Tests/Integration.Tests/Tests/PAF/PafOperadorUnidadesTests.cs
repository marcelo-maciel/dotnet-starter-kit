using FSH.Modules.PAF.Contracts.v1.Contratos;
using FSH.Modules.PAF.Contracts.v1.Unidades;
using FSH.Modules.PAF.Features.v1.Contratos;
using FSH.Modules.PAF.Provisionamento;
using FSH.Modules.PAF.Sessao;
using FSH.Modules.Identity.Contracts.Services;
using Integration.Tests.Infrastructure;

namespace Integration.Tests.Tests.PAF;

/// <summary>
/// RF23 and RF22 (C08): the Codefi operator reads the unidades and contracts of one client tenant at a time, the one
/// in the tenant header, and writes the contracts; validities of one unidade never overlap, also under concurrency.
/// </summary>
[Collection(FshCollectionDefinition.Name)]
public sealed class PafOperadorUnidadesTests(FshWebApplicationFactory factory) : IAsyncLifetime
{
    private const string RotaOperador = "/operador/unidades";

    private readonly AuthHelper _auth = new(factory);
    private readonly PafApi _api = new(factory);
    private readonly PafTenantCliente _tenantB = new(factory, "paf-c08");

    public Task InitializeAsync() => _tenantB.InicializarAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task RaizComTenantB_ListaSoAsUnidadesDeB_InicialPrimeiro_ComTodosOsContratos()
    {
        using var raizNaRaiz = await _api.RootAsync();
        var unidadeDaRaiz = await _api.CriarUnidadeAsync(raizNaRaiz);
        var segunda = await _tenantB.CriarUnidadeAsync(limiteSessoes: null);
        using var raiz = await RaizNoTenantAsync(_tenantB.TenantId);
        await CriarContratoAsync(raiz, segunda.UnidadeId, new DateOnly(2025, 1, 1), new DateOnly(2025, 12, 31), 3);
        await CriarContratoAsync(raiz, segunda.UnidadeId, new DateOnly(2026, 1, 1), null, 7);

        var unidades = await PafApi.OkAsync<List<UnidadeOperadorDto>>(raiz, HttpMethod.Get, RotaOperador, null, null);

        unidades.Select(u => u.Id).ShouldBe([_tenantB.UnidadeInicial, segunda.UnidadeId]);
        unidades.ShouldNotContain(u => u.Id == unidadeDaRaiz);
        unidades[0].Inicial.ShouldBeTrue();
        unidades[0].Contratos.ShouldHaveSingleItem().LimiteSessoes.ShouldBe(PafTenantCliente.LimiteDaUnidadeInicial);
        unidades[1].Inicial.ShouldBeFalse();
        unidades[1].Contratos.Select(c => c.LimiteSessoes).ShouldBe([7, 3], "newest start first");
        unidades[1].Contratos.ShouldAllBe(c => c.UnidadeId == segunda.UnidadeId);

        // The root tenant's own list does not carry the client's unidades either: one tenant per request.
        var daRaiz = await PafApi.OkAsync<List<UnidadeOperadorDto>>(raizNaRaiz, HttpMethod.Get, RotaOperador, null, null);
        daRaiz.ShouldContain(u => u.Id == unidadeDaRaiz);
        daRaiz.ShouldNotContain(u => u.Id == segunda.UnidadeId || u.Id == _tenantB.UnidadeInicial);
    }

    [Fact]
    public async Task AdminDoCliente_403NaListaDoOperadorENaGravacaoDeContrato()
    {
        using var lista = await PafApi.EnviarAsync(_tenantB.Admin, HttpMethod.Get, RotaOperador, null, null);
        lista.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        using var contrato = await PafApi.EnviarAsync(_tenantB.Admin, HttpMethod.Post, "/contratos", null,
            Criar(_tenantB.UnidadeInicial, new DateOnly(2030, 1, 1), null, 999));
        contrato.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task ContratoEmUnidadeDeOutroTenant_404NaCriacaoENaAlteracao()
    {
        using var raizNaRaiz = await _api.RootAsync();
        var unidadeDeA = await _api.CriarUnidadeAsync(raizNaRaiz);
        var contratoDeA = await CriarContratoAsync(raizNaRaiz, unidadeDeA, new DateOnly(2026, 1, 1), null, 2);
        using var raizEmB = await RaizNoTenantAsync(_tenantB.TenantId);

        using var criar = await PafApi.EnviarAsync(raizEmB, HttpMethod.Post, "/contratos", null,
            Criar(unidadeDeA, new DateOnly(2030, 1, 1), null, 5));
        criar.StatusCode.ShouldBe(HttpStatusCode.NotFound, await criar.Content.ReadAsStringAsync());

        using var alterar = await PafApi.EnviarAsync(raizEmB, HttpMethod.Put, $"/contratos/{contratoDeA}", null,
            new UpdateContratoUnidadeCommand(contratoDeA, new DateOnly(2026, 1, 1), null, "Invadido", 999, "Recusar"));
        alterar.StatusCode.ShouldBe(HttpStatusCode.NotFound, await alterar.Content.ReadAsStringAsync());

        var deA = await PafApi.OkAsync<List<UnidadeOperadorDto>>(raizNaRaiz, HttpMethod.Get, RotaOperador, null, null);
        deA.Single(u => u.Id == unidadeDeA).Contratos.ShouldHaveSingleItem().LimiteSessoes.ShouldBe(2);
    }

    [Fact]
    public async Task FimAntesDoInicio_400()
    {
        var unidade = await _tenantB.CriarUnidadeAsync(limiteSessoes: null);
        using var raiz = await RaizNoTenantAsync(_tenantB.TenantId);

        using var resposta = await PafApi.EnviarAsync(raiz, HttpMethod.Post, "/contratos", null,
            Criar(unidade.UnidadeId, new DateOnly(2026, 6, 1), new DateOnly(2026, 5, 31), 1));
        resposta.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task VigenciaSobreposta_409_DiasDasPontasInclusivos_FimAbertoCobreOFuturo_OutraUnidadeLivre()
    {
        var unidade = await _tenantB.CriarUnidadeAsync(limiteSessoes: null);
        var outra = await _tenantB.CriarUnidadeAsync(limiteSessoes: null);
        using var raiz = await RaizNoTenantAsync(_tenantB.TenantId);
        var primeiro = await CriarContratoAsync(raiz, unidade.UnidadeId, new DateOnly(2026, 1, 1), new DateOnly(2026, 6, 30), 3);

        await SobrepostoAsync(raiz, HttpMethod.Post, "/contratos", Criar(unidade.UnidadeId, new DateOnly(2026, 6, 30), null, 4));
        await SobrepostoAsync(raiz, HttpMethod.Post, "/contratos", Criar(unidade.UnidadeId, new DateOnly(2025, 1, 1), null, 4));
        await SobrepostoAsync(raiz, HttpMethod.Post, "/contratos",
            Criar(unidade.UnidadeId, new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 31), 4));

        var seguinte = await CriarContratoAsync(raiz, unidade.UnidadeId, new DateOnly(2026, 7, 1), null, 4);
        await SobrepostoAsync(raiz, HttpMethod.Post, "/contratos",
            Criar(unidade.UnidadeId, new DateOnly(2031, 1, 1), new DateOnly(2031, 12, 31), 4));

        // The update compares against the other contracts only: shrinking itself passes, reaching the next one fails.
        await PafApi.OkAsync<Guid>(raiz, HttpMethod.Put, $"/contratos/{primeiro}", null,
            new UpdateContratoUnidadeCommand(primeiro, new DateOnly(2026, 2, 1), new DateOnly(2026, 6, 30), "Pro", 5, "Recusar"));
        await SobrepostoAsync(raiz, HttpMethod.Put, $"/contratos/{primeiro}",
            new UpdateContratoUnidadeCommand(primeiro, new DateOnly(2026, 2, 1), new DateOnly(2026, 7, 1), "Pro", 5, "Recusar"));
        await SobrepostoAsync(raiz, HttpMethod.Put, $"/contratos/{seguinte}",
            new UpdateContratoUnidadeCommand(seguinte, new DateOnly(2026, 6, 30), null, "Pro", 4, "Recusar"));

        await CriarContratoAsync(raiz, outra.UnidadeId, new DateOnly(2026, 1, 1), null, 9);

        var unidades = await PafApi.OkAsync<List<UnidadeOperadorDto>>(raiz, HttpMethod.Get, RotaOperador, null, null);
        var contratos = unidades.Single(u => u.Id == unidade.UnidadeId).Contratos;
        contratos.Select(c => (c.InicioVigencia, c.FimVigencia, c.LimiteSessoes)).ShouldBe(
        [
            (new DateOnly(2026, 7, 1), (DateOnly?)null, 4),
            (new DateOnly(2026, 2, 1), new DateOnly(2026, 6, 30), 5),
        ]);
    }

    [Fact]
    public async Task DoisCreatesSobrepostosAoMesmoTempo_UmGravaEOOutro409()
    {
        using var raizA = await RaizNoTenantAsync(_tenantB.TenantId);
        using var raizB = await RaizNoTenantAsync(_tenantB.TenantId);

        // Several rounds, each on a fresh unidade: one lucky interleaving would not prove the lock.
        for (var rodada = 0; rodada < 8; rodada++)
        {
            var unidade = await _tenantB.CriarUnidadeAsync(limiteSessoes: null);
            var respostas = await Task.WhenAll(
                PafApi.EnviarAsync(raizA, HttpMethod.Post, "/contratos", null, Criar(unidade.UnidadeId, new DateOnly(2026, 1, 1), null, 1)),
                PafApi.EnviarAsync(raizB, HttpMethod.Post, "/contratos", null, Criar(unidade.UnidadeId, new DateOnly(2026, 5, 1), new DateOnly(2026, 5, 31), 2)));
            var estados = respostas.Select(r => r.StatusCode).Order().ToList();
            foreach (var resposta in respostas)
            {
                resposta.Dispose();
            }

            var unidades = await PafApi.OkAsync<List<UnidadeOperadorDto>>(raizA, HttpMethod.Get, RotaOperador, null, null);
            unidades.Single(u => u.Id == unidade.UnidadeId).Contratos.Count.ShouldBe(1, $"rodada {rodada}");
            estados.ShouldBe([HttpStatusCode.OK, HttpStatusCode.Conflict], $"rodada {rodada}");
        }
    }

    [Fact]
    public async Task TenantNovo_ContratoPadraoAceitaLogin_OperadorEncerraERecusa_ReabreEAceita()
    {
        var tenantId = $"paf-c08n-{Guid.NewGuid().ToString("N")[..8]}";
        var adminEmail = $"admin-{tenantId}@paf.test";
        using (var root = await _auth.CreateRootAdminClientAsync())
        {
            using var criar = await root.PostAsJsonAsync(TestConstants.TenantsBasePath, new
            {
                id = tenantId,
                name = $"Tenant {tenantId}",
                connectionString = (string?)null,
                adminEmail,
                adminPassword = TestConstants.DefaultPassword,
                issuer = $"{tenantId}.issuer",
            });
            criar.StatusCode.ShouldBe(HttpStatusCode.Created, await criar.Content.ReadAsStringAsync());
            await TenantProvisioningWait.WaitForProvisioningAsync(root, tenantId);
        }

        await OutboxDrain.DrainAsync(factory.Services);
        await EsperarAdministradorAssociadoAsync(tenantId, adminEmail);

        // RF23 (21/10/2026): the default contract the initial unidade is born with lets the admin in at once.
        using var padrao = await LoginAsync(tenantId, adminEmail);
        padrao.StatusCode.ShouldBe(HttpStatusCode.OK, await padrao.Content.ReadAsStringAsync());

        using var raiz = await RaizNoTenantAsync(tenantId);
        var inicial = (await PafApi.OkAsync<List<UnidadeOperadorDto>>(raiz, HttpMethod.Get, RotaOperador, null, null)).ShouldHaveSingleItem();
        inicial.Inicial.ShouldBeTrue();
        var contrato = inicial.Contratos.ShouldHaveSingleItem();
        (contrato.Plano, contrato.LimiteSessoes, contrato.Politica, contrato.FimVigencia).ShouldBe(("Inicial", 5, "Recusar", (DateOnly?)null));

        // The operator owns it: ended yesterday, the client is refused with the contract message; reopened, let in.
        var hoje = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeBySystemTimeZoneId(DateTime.UtcNow, "America/Sao_Paulo"));
        await PafApi.OkAsync<Guid>(raiz, HttpMethod.Put, $"/contratos/{contrato.Id}", null,
            new UpdateContratoUnidadeCommand(contrato.Id, hoje.AddDays(-2), hoje.AddDays(-1), "Inicial", 5, "Recusar"));
        using var encerrado = await LoginAsync(tenantId, adminEmail);
        encerrado.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        PafSessaoLicencaTests.Detalhe(await encerrado.Content.ReadAsStringAsync()).ShouldBe(Licencas.MensagemSemContrato);

        await PafApi.OkAsync<Guid>(raiz, HttpMethod.Put, $"/contratos/{contrato.Id}", null,
            new UpdateContratoUnidadeCommand(contrato.Id, hoje.AddDays(-2), null, "Inicial", 5, "Recusar"));
        using var reaberto = await LoginAsync(tenantId, adminEmail);
        reaberto.StatusCode.ShouldBe(HttpStatusCode.OK, await reaberto.Content.ReadAsStringAsync());
    }

    // ─── helpers ────────────────────────────────────────────────────────

    private static CreateContratoUnidadeCommand Criar(Guid unidadeId, DateOnly inicio, DateOnly? fim, int limite) =>
        new(unidadeId, inicio, fim, "Teste", limite, "Recusar");

    private static Task<Guid> CriarContratoAsync(HttpClient raiz, Guid unidadeId, DateOnly inicio, DateOnly? fim, int limite) =>
        PafApi.OkAsync<Guid>(raiz, HttpMethod.Post, "/contratos", null, Criar(unidadeId, inicio, fim, limite));

    private static async Task SobrepostoAsync(HttpClient raiz, HttpMethod metodo, string rota, object corpo)
    {
        using var resposta = await PafApi.EnviarAsync(raiz, metodo, rota, null, corpo);
        var texto = await resposta.Content.ReadAsStringAsync();
        resposta.StatusCode.ShouldBe(HttpStatusCode.Conflict, texto);
        PafSessaoLicencaTests.Detalhe(texto).ShouldBe(VigenciaContrato.MensagemSobreposta);
    }

    /// <summary>The Codefi operator: root token, explicit tenant header of the client (arquitetura seção 6).</summary>
    private async Task<HttpClient> RaizNoTenantAsync(string tenantId)
    {
        var token = await _auth.GetRootAdminTokenAsync();
        var cliente = factory.CreateClient();
        cliente.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
        cliente.DefaultRequestHeaders.Add("tenant", tenantId);
        return cliente;
    }

    private async Task<HttpResponseMessage> LoginAsync(string tenantId, string email)
    {
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{TestConstants.IdentityBasePath}/token/issue");
        request.Headers.Add("tenant", tenantId);
        request.Content = JsonContent.Create(new { email, password = TestConstants.DefaultPassword });
        return await client.SendAsync(request);
    }

    private async Task EsperarAdministradorAssociadoAsync(string tenantId, string adminEmail)
    {
        var tenant = await ProvisionamentoTenantPaf.LerTenantAsync(factory.Services.GetRequiredService<IServiceScopeFactory>(), tenantId)
            ?? throw new InvalidOperationException($"Tenant '{tenantId}' não existe.");
        var limite = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < limite)
        {
            await using var escopo = ProvisionamentoTenantPaf.AbrirEscopo(factory.Services.GetRequiredService<IServiceScopeFactory>(), tenant);
            var usuarios = await escopo.ServiceProvider.GetRequiredService<IUserService>().GetListAsync(CancellationToken.None);
            var adminId = Guid.Parse(usuarios.Single(u => u.Email == adminEmail).Id!);
            var db = escopo.ServiceProvider.GetRequiredService<FSH.Modules.PAF.Data.PafDbContext>();
            if (await db.UsuariosUnidade.AnyAsync(u => u.UsuarioId == adminId && u.Ordem == 0))
            {
                return;
            }

            await Task.Delay(250);
        }

        throw new TimeoutException($"Tenant '{tenantId}': o job não associou o administrador inicial em 30 s.");
    }
}
