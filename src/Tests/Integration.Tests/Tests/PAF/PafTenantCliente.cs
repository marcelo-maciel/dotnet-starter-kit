using System.IdentityModel.Tokens.Jwt;
using Finbuckle.MultiTenant;
using Finbuckle.MultiTenant.Abstractions;
using FSH.Framework.Shared.Multitenancy;
using FSH.Modules.PAF.Data;
using FSH.Modules.PAF.Domain;
using FSH.Modules.PAF.Escopo;
using Integration.Tests.Infrastructure;
using Integration.Tests.Tests.Sessions;

namespace Integration.Tests.Tests.PAF;

/// <summary>Unidade de teste num tenant de cliente, com dois guichês.</summary>
internal sealed record UnidadeTeste(Guid UnidadeId, Guid GuicheA, Guid GuicheB);

/// <summary>
/// Tenant de cliente provisionado pela API do kit, onde a licença vale (o tenant raiz é isento, RF23), com
/// acesso direto ao PafDbContext dele para montar unidades, contratos e escopos.
/// </summary>
internal sealed class PafTenantCliente(FshWebApplicationFactory factory, string prefixo)
{
    private readonly AuthHelper _auth = new(factory);
    private AppTenantInfo? _tenant;

    public string TenantId { get; } = $"{prefixo}-{Guid.NewGuid().ToString("N")[..8]}";

    public HttpClient Admin { get; private set; } = default!;

    public async Task InicializarAsync()
    {
        using var root = await _auth.CreateRootAdminClientAsync();
        var adminEmail = $"admin-{TenantId}@paf.test";
        var criar = await root.PostAsJsonAsync(TestConstants.TenantsBasePath, new
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
        var token = await LoginAsync(adminEmail, TestConstants.DefaultPassword);
        token.StatusCode.ShouldBe(HttpStatusCode.OK, await token.Content.ReadAsStringAsync());
        Admin = Cliente((await token.Content.ReadFromJsonAsync<TokenResult>())!.AccessToken);

        using var scope = factory.Services.CreateScope();
        _tenant = await scope.ServiceProvider.GetRequiredService<IMultiTenantStore<AppTenantInfo>>().GetAsync(TenantId);
        _tenant.ShouldNotBeNull();
    }

    // Synchronous on purpose: the Finbuckle context is an AsyncLocal that would not flow back from an async helper.
    public PafDbContext Contexto(IServiceScope scope, Guid? unidadeCorrente)
    {
        scope.ServiceProvider.GetRequiredService<IMultiTenantContextSetter>().MultiTenantContext =
            new MultiTenantContext<AppTenantInfo>(_tenant ?? throw new InvalidOperationException("InicializarAsync não foi chamado."));
        if (unidadeCorrente is { } unidadeId)
        {
            scope.ServiceProvider.GetRequiredService<IUnidadeCorrente>().Definir(unidadeId);
        }

        return scope.ServiceProvider.GetRequiredService<PafDbContext>();
    }

    /// <summary>Unidade ativa; com <paramref name="limiteSessoes"/> nulo, sem contrato (sem saldo).</summary>
    public async Task<UnidadeTeste> CriarUnidadeAsync(int? limiteSessoes)
    {
        using var scope = factory.Services.CreateScope();
        var db = Contexto(scope, unidadeCorrente: null);
        var unidade = Unidade.Criar($"Loja {Guid.NewGuid():N}", documentoFiscal: null);
        var guicheA = Estacao.Criar(unidade.Id, "Guichê 1", TipoEstacao.Guiche);
        var guicheB = Estacao.Criar(unidade.Id, "Guichê 2", TipoEstacao.Guiche);
        db.Unidades.Add(unidade);
        db.Estacoes.AddRange(guicheA, guicheB);
        if (limiteSessoes is { } limite)
        {
            // Started two days ago so the America/Sao_Paulo calendar day is inside the validity at any UTC hour.
            var inicio = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-2));
            db.ContratosUnidade.Add(ContratoUnidade.Criar(unidade.Id, inicio, null, "Teste", limite, PoliticaLimiteLicenca.Recusar));
        }

        await db.SaveChangesAsync();
        return new UnidadeTeste(unidade.Id, guicheA.Id, guicheB.Id);
    }

    /// <summary>Usuário com login confirmado e escopo nas unidades dadas, a primeira como principal.</summary>
    public async Task<SeededUser> CriarUsuarioAsync(params Guid[] unidades)
    {
        var usuario = await IdentityUserSeeder.CreateLoginableUserAsync(factory, Admin, "paf", TenantId);
        using var scope = factory.Services.CreateScope();
        var db = Contexto(scope, unidadeCorrente: null);
        for (var ordem = 0; ordem < unidades.Length; ordem++)
        {
            db.UsuariosUnidade.Add(UsuarioUnidade.Criar(Guid.Parse(usuario.UserId), unidades[ordem], ordem));
        }

        await db.SaveChangesAsync();
        return usuario;
    }

    public async Task<HttpResponseMessage> LoginAsync(string email, string senha)
    {
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{TestConstants.IdentityBasePath}/token/issue");
        request.Headers.Add("tenant", TenantId);
        request.Content = JsonContent.Create(new { email, password = senha });
        return await client.SendAsync(request);
    }

    public async Task<TokenResult> EntrarAsync(SeededUser usuario)
    {
        var resposta = await LoginAsync(usuario.Email, usuario.Password);
        resposta.StatusCode.ShouldBe(HttpStatusCode.OK, await resposta.Content.ReadAsStringAsync());
        return (await resposta.Content.ReadFromJsonAsync<TokenResult>())!;
    }

    public async Task<HttpResponseMessage> RenovarAsync(TokenResult token)
    {
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{TestConstants.IdentityBasePath}/token/refresh");
        request.Headers.Add("tenant", TenantId);
        request.Content = JsonContent.Create(new { token = token.AccessToken, refreshToken = token.RefreshToken });
        return await client.SendAsync(request);
    }

    public HttpClient Cliente(string accessToken)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        client.DefaultRequestHeaders.Add("tenant", TenantId);
        return client;
    }

    public static Guid SessaoDoToken(string accessToken) =>
        Guid.Parse(new JwtSecurityTokenHandler().ReadJwtToken(accessToken).Claims.Single(c => c.Type == "sid").Value);

    /// <summary>Linhas de AtividadeSessao do usuário, lidas sem filtro de unidade (a tabela não tem).</summary>
    public async Task<List<AtividadeSessao>> AtividadesAsync(Guid usuarioId)
    {
        using var scope = factory.Services.CreateScope();
        var db = Contexto(scope, unidadeCorrente: null);
        return await db.AtividadesSessao.AsNoTracking().Where(a => a.UsuarioId == usuarioId).ToListAsync();
    }

    public async Task<int> ExecutarAsync(FormattableString sql)
    {
        using var scope = factory.Services.CreateScope();
        var db = Contexto(scope, unidadeCorrente: null);
        return await db.Database.ExecuteSqlAsync(sql);
    }

    /// <summary>Atendimento chamado no guichê A, que deixa esse guichê com atendimento em curso.</summary>
    public async Task ChamarNoGuicheAsync(UnidadeTeste unidade)
    {
        using var scope = factory.Services.CreateScope();
        var db = Contexto(scope, unidadeCorrente: unidade.UnidadeId);
        var fila = Fila.Criar(unidade.UnidadeId, "Caixa", "Caixa", setorId: null);
        var prioridade = Prioridade.Criar(unidade.UnidadeId, "Normal", valor: 0);
        var ficha = Ficha.Criar(unidade.UnidadeId, fila.Id, prioridade.Id, "A", 1, CanalEmissao.Totem, unidade.GuicheA, Guid.NewGuid().ToString("N"));
        var atendimento = Atendimento.Criar(unidade.UnidadeId, ficha.Id, fila.Id, prioridade.Id, atendimentoOrigemId: null);
        db.Filas.Add(fila);
        db.Prioridades.Add(prioridade);
        db.FilaPrioridades.Add(FilaPrioridade.Criar(unidade.UnidadeId, fila.Id, prioridade.Id, "A", numeroInicial: 1, ordem: 1));
        db.Fichas.Add(ficha);
        db.Atendimentos.Add(atendimento);
        await db.SaveChangesAsync();
        (await PafBanco.ChamarAsync(db, atendimento.Id, unidade.GuicheA)).ShouldBe(1);
    }
}
