using System.IdentityModel.Tokens.Jwt;
using Finbuckle.MultiTenant;
using Finbuckle.MultiTenant.Abstractions;
using FSH.Framework.Shared.Multitenancy;
using FSH.Modules.Identity.Domain;
using FSH.Modules.PAF.Contracts.Authorization;
using FSH.Modules.PAF.Contracts.v1.Estacoes;
using FSH.Modules.PAF.Data;
using FSH.Modules.PAF.Domain;
using FSH.Modules.PAF.Escopo;
using Mediator;
using Microsoft.AspNetCore.Identity;

namespace PAF.Kestrel.Tests;

/// <summary>
/// Unidade with one fila (Caixa, Normal "A"), one guichê serving it, a totem emitting it and a painel showing it,
/// and a contract with balance, so a person can log in, select the guichê and call.
/// </summary>
internal sealed record CenarioUnidade(Guid UnidadeId, Guid FilaId, Guid PrioridadeId, Guid Guiche, Guid Totem, Guid Painel);

/// <summary>A person of the unidade with the password to log in again (each login is a new kit session).</summary>
internal sealed record AtendenteCriado(Guid UsuarioId, string Email, string Senha);

/// <summary>
/// Client tenant provisioned through the kit API, where the licence applies (the root tenant is exempt), every
/// request over the Kestrel socket. Mirrors Integration.Tests' PafTenantCliente and PafOperacao for what O16 needs.
/// </summary>
internal sealed class TenantPaf(PafKestrelFactory factory)
{
    private AppTenantInfo? _tenant;

    public string TenantId { get; } = $"paf-ws-{Guid.NewGuid().ToString("N")[..8]}";

    public HttpClient Admin { get; private set; } = default!;

    public async Task InicializarAsync()
    {
        using var root = Cliente(await EntrarAsync(TestConstants.RootTenantId, TestConstants.RootAdminEmail, TestConstants.DefaultPassword), TestConstants.RootTenantId);
        var adminEmail = $"admin-{TenantId}@paf.test";
        using var criar = await root.PostAsJsonAsync(TestConstants.TenantsBasePath, new
        {
            id = TenantId,
            name = $"Tenant {TenantId}",
            connectionString = (string?)null,
            adminEmail,
            adminPassword = TestConstants.DefaultPassword,
            issuer = $"{TenantId}.issuer",
        });
        criar.StatusCode.ShouldBe(HttpStatusCode.Created, await criar.Content.ReadAsStringAsync());
        await TenantProvisioningWait.WaitForProvisioningAsync(root, TenantId);

        // The admin has no UsuarioUnidade: he is not a PAF user and takes no licence.
        Admin = Cliente(await EntrarAsync(TenantId, adminEmail, TestConstants.DefaultPassword), TenantId);

        using var scope = factory.Services.CreateScope();
        _tenant = await scope.ServiceProvider.GetRequiredService<IMultiTenantStore<AppTenantInfo>>().GetAsync(TenantId);
        _tenant.ShouldNotBeNull();
    }

    // Synchronous on purpose: the Finbuckle context is an AsyncLocal that would not flow back from an async helper.
    public PafDbContext Contexto(IServiceScope scope, Guid? unidadeCorrente)
    {
        DefinirTenant(scope);
        if (unidadeCorrente is { } unidadeId)
        {
            scope.ServiceProvider.GetRequiredService<IUnidadeCorrente>().Definir(unidadeId);
        }

        return scope.ServiceProvider.GetRequiredService<PafDbContext>();
    }

    public void DefinirTenant(IServiceScope scope) =>
        scope.ServiceProvider.GetRequiredService<IMultiTenantContextSetter>().MultiTenantContext =
            new MultiTenantContext<AppTenantInfo>(_tenant ?? throw new InvalidOperationException("InicializarAsync não foi chamado."));

    public async Task<CenarioUnidade> CriarUnidadeAsync()
    {
        using var scope = factory.Services.CreateScope();
        var db = Contexto(scope, unidadeCorrente: null);
        var unidade = Unidade.Criar($"Loja {Guid.NewGuid():N}", documentoFiscal: null);
        var u = unidade.Id;
        var caixa = Fila.Criar(u, "Caixa", "Caixa", setorId: null);
        var normal = Prioridade.Criar(u, "Normal", valor: 0);
        var guiche = Estacao.Criar(u, "Guichê 1", TipoEstacao.Guiche);
        var totem = Estacao.Criar(u, "Totem", TipoEstacao.Emissao);
        var painel = Estacao.Criar(u, "Painel Caixa", TipoEstacao.Painel);

        db.Unidades.Add(unidade);
        db.Filas.Add(caixa);
        db.Prioridades.Add(normal);
        db.Estacoes.AddRange(guiche, totem, painel);
        db.FilaPrioridades.Add(FilaPrioridade.Criar(u, caixa.Id, normal.Id, "A", numeroInicial: 1, ordem: 0));
        db.EstacaoFilas.AddRange(
            EstacaoFila.Criar(u, guiche.Id, PapelEstacao.Guiche, caixa.Id, 0),
            EstacaoFila.Criar(u, totem.Id, PapelEstacao.Emissao, caixa.Id, 0),
            EstacaoFila.Criar(u, painel.Id, PapelEstacao.Painel, caixa.Id, 0));
        db.EstacaoPrioridades.Add(EstacaoPrioridade.Criar(u, guiche.Id, normal.Id, 0));
        // Started two days ago so the America/Sao_Paulo calendar day is inside the validity at any UTC hour.
        db.ContratosUnidade.Add(ContratoUnidade.Criar(u, DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-2)), null, "Teste", 5, PoliticaLimiteLicenca.Recusar));
        await db.SaveChangesAsync();
        return new CenarioUnidade(u, caixa.Id, normal.Id, guiche.Id, totem.Id, painel.Id);
    }

    /// <summary>Station credential of the given role, from a link redeemed through the anonymous endpoint (the device's path).</summary>
    public async Task<CredencialEstacaoDto> CredencialAsync(Guid unidadeId, Guid estacaoId, string papel)
    {
        VinculoEstacaoGeradoDto link;
        using (var scope = factory.Services.CreateScope())
        {
            Contexto(scope, unidadeId);
            link = await scope.ServiceProvider.GetRequiredService<IMediator>().Send(new GerarVinculoEstacaoCommand(estacaoId));
        }

        using var client = factory.Cliente();
        client.DefaultRequestHeaders.Add(MultitenancyConstants.Identifier, TenantId);
        using var resgate = await client.PostAsJsonAsync("/api/v1/paf/estacoes/vinculo/resgatar", new { token = link.Token });
        resgate.StatusCode.ShouldBe(HttpStatusCode.OK, await resgate.Content.ReadAsStringAsync());
        var credenciais = await resgate.Content.ReadFromJsonAsync<CredenciaisEstacaoDto>();
        return credenciais!.Credenciais.Single(c => c.Papel == papel);
    }

    /// <summary>New access token from the long-lived secret, as the totem and the painel renew.</summary>
    public async Task<AcessoEstacaoDto> RenovarCredencialAsync(string segredo)
    {
        using var client = factory.Cliente();
        client.DefaultRequestHeaders.Add(MultitenancyConstants.Identifier, TenantId);
        using var resposta = await client.PostAsJsonAsync("/api/v1/paf/estacoes/credencial/renovar", new RenovarCredencialEstacaoCommand(segredo));
        resposta.StatusCode.ShouldBe(HttpStatusCode.OK, await resposta.Content.ReadAsStringAsync());
        return (await resposta.Content.ReadFromJsonAsync<AcessoEstacaoDto>())!;
    }

    /// <summary>Confirmed user of the unidade (principal), logged in; returns the access token.</summary>
    public async Task<string> AtendenteAsync(Guid unidadeId) => await EntrarAsync(await CriarAtendenteAsync(unidadeId));

    /// <summary>Access token of a new login of the person (a new kit session, as on another machine).</summary>
    public Task<string> EntrarAsync(AtendenteCriado atendente) => EntrarAsync(TenantId, atendente.Email, atendente.Senha);

    /// <summary>Licence activity rows of the person (one per live session).</summary>
    public async Task<List<AtividadeSessao>> AtividadesAsync(Guid usuarioId)
    {
        using var scope = factory.Services.CreateScope();
        var db = Contexto(scope, unidadeCorrente: null);
        return await db.AtividadesSessao.AsNoTracking().Where(a => a.UsuarioId == usuarioId).ToListAsync();
    }

    /// <summary>
    /// Delivers the outbox now: this host has no dispatcher, so the kit's events reach the PAF only when a test
    /// drains, which is how a test controls when the kit's revocation event arrives.
    /// </summary>
    public Task DrenarCaixaDeSaidaAsync() => OutboxDrain.DrainAsync(factory.Services);

    /// <summary>Confirmed user of the unidade (principal), not logged in yet.</summary>
    public async Task<AtendenteCriado> CriarAtendenteAsync(Guid unidadeId)
    {
        var sufixo = Guid.NewGuid().ToString("N")[..8];
        var email = $"paf-ws-{sufixo}@example.com";
        const string senha = "Test@1234!";
        using var registro = await Admin.PostAsJsonAsync($"{TestConstants.IdentityBasePath}/register", new
        {
            firstName = "Atendente",
            lastName = "WebSocket",
            email,
            userName = $"paf-ws-{sufixo}",
            password = senha,
            confirmPassword = senha,
        });
        registro.StatusCode.ShouldBe(HttpStatusCode.Created, await registro.Content.ReadAsStringAsync());
        var usuarioId = (await registro.Content.ReadFromJsonAsync<RegisterResult>())!.UserId;

        using (var scope = factory.Services.CreateScope())
        {
            var db = Contexto(scope, unidadeCorrente: null);
            var usuarios = scope.ServiceProvider.GetRequiredService<UserManager<FshUser>>();
            var usuario = await usuarios.FindByIdAsync(usuarioId);
            usuario.ShouldNotBeNull();
            (await usuarios.ConfirmEmailAsync(usuario, await usuarios.GenerateEmailConfirmationTokenAsync(usuario))).Succeeded.ShouldBeTrue();
            db.UsuariosUnidade.Add(UsuarioUnidade.Criar(Guid.Parse(usuarioId), unidadeId, 0));
            await db.SaveChangesAsync();
        }

        return new AtendenteCriado(Guid.Parse(usuarioId), email, senha);
    }

    /// <summary>Moves the unidade's fichas past the five-second call guard (seção 4) without sleeping.</summary>
    public async Task EnvelhecerFichasAsync(Guid unidadeId)
    {
        using var scope = factory.Services.CreateScope();
        var db = Contexto(scope, unidadeCorrente: null);
        (await db.Database.ExecuteSqlAsync(
            $"""UPDATE paf."Fichas" SET "CriadoEm" = "CriadoEm" - interval '6 seconds' WHERE "UnidadeId" = {unidadeId}""")).ShouldBeGreaterThan(0);
    }

    public HttpClient Cliente(string accessToken) => Cliente(accessToken, TenantId);

    public static Guid SessaoDoToken(string accessToken) =>
        Guid.Parse(new JwtSecurityTokenHandler().ReadJwtToken(accessToken).Claims.Single(c => c.Type == "sid").Value);

    private HttpClient Cliente(string accessToken, string tenant)
    {
        var client = factory.Cliente();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        client.DefaultRequestHeaders.Add(MultitenancyConstants.Identifier, tenant);
        return client;
    }

    private async Task<string> EntrarAsync(string tenant, string email, string senha)
    {
        using var client = factory.Cliente();
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{TestConstants.IdentityBasePath}/token/issue");
        request.Headers.Add(MultitenancyConstants.Identifier, tenant);
        request.Content = JsonContent.Create(new { email, password = senha });
        using var resposta = await client.SendAsync(request);
        resposta.StatusCode.ShouldBe(HttpStatusCode.OK, await resposta.Content.ReadAsStringAsync());
        return (await resposta.Content.ReadFromJsonAsync<TokenResult>())!.AccessToken;
    }
}
