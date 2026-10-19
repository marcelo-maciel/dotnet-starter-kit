using Finbuckle.MultiTenant;
using Finbuckle.MultiTenant.Abstractions;
using FSH.Framework.Shared.Multitenancy;
using FSH.Modules.Identity.Domain;
using FSH.Modules.PAF.Contracts.Authorization;
using FSH.Modules.PAF.Contracts.v1.Estacoes;
using FSH.Modules.PAF.Contracts.v1.Fichas;
using FSH.Modules.PAF.Data;
using FSH.Modules.PAF.Domain;
using FSH.Modules.PAF.Escopo;
using Mediator;
using Microsoft.AspNetCore.Identity;

namespace PAF.Latencia.Tests;

/// <summary>One unidade of the reference volume: its fila, prioridade and stations.</summary>
internal sealed record UnidadeCarga(Guid Id, Guid FilaId, Guid PrioridadeId, IReadOnlyList<Guid> Guiches, IReadOnlyList<Guid> Totens, IReadOnlyList<Guid> Paineis);

/// <summary>A logged-in person with the guichê selected; <see cref="Cliente"/> talks to the instance that calls.</summary>
internal sealed record AtendenteCarga(Guid UnidadeId, Guid Guiche, string Token, HttpClient Cliente);

/// <summary>Station access token of a totem or a painel.</summary>
internal sealed record EstacaoCarga(Guid UnidadeId, Guid EstacaoId, string Token);

/// <summary>
/// The reference volume of D20 (REQ 8.2, 17/10/2026): one client with 20 unidades and, per unidade, 3 totens,
/// 2 painéis, 8 guichês and 600 fichas a day. Built through the API where the device or the person would (tenant,
/// registration, login, link redemption, guichê selection, emission) and through the DbContext for the cadastros.
/// </summary>
internal sealed class CargaReferencia(HostLatencia host)
{
    public const int Unidades = 20;
    public const int TotensPorUnidade = 3;
    public const int PaineisPorUnidade = 2;
    public const int GuichesPorUnidade = 8;

    // Registration hashes a password per person: bounded so the setup does not starve the host it measures.
    private const int ParalelismoPreparo = 8;

    private AppTenantInfo? _tenant;

    public string TenantId { get; } = $"paf-lat-{Guid.NewGuid().ToString("N")[..8]}";

    public HttpClient Admin { get; private set; } = default!;

    public List<UnidadeCarga> UnidadesCriadas { get; } = [];

    public List<AtendenteCarga> Atendentes { get; } = [];

    public List<EstacaoCarga> Totens { get; } = [];

    public List<EstacaoCarga> Paineis { get; } = [];

    public async Task PrepararAsync(int fichasPorUnidade)
    {
        await CriarTenantAsync();
        for (var i = 0; i < Unidades; i++)
        {
            UnidadesCriadas.Add(await CriarUnidadeAsync(i));
        }

        using var limite = new SemaphoreSlim(ParalelismoPreparo);
        var estacoes = UnidadesCriadas.SelectMany(u =>
            u.Totens.Select(t => (u.Id, t, PafPapeisEstacao.Emissao)).Concat(u.Paineis.Select(p => (u.Id, p, PafPapeisEstacao.Painel))));
        foreach (var credencial in await Task.WhenAll(estacoes.Select(e => Limitado(limite, () => CredencialAsync(e.Item1, e.Item2, e.Item3)))))
        {
            (credencial.Papel == PafPapeisEstacao.Painel ? Paineis : Totens).Add(credencial.Estacao);
        }

        var vagas = UnidadesCriadas.SelectMany(u => u.Guiches.Select(g => (u.Id, g)));
        Atendentes.AddRange(await Task.WhenAll(vagas.Select(v => Limitado(limite, () => AtendenteAsync(v.Item1, v.Item2)))));

        // In sequence inside each unidade, unidades in parallel: a fila gets one emission at a time, as from its
        // totens at D20's pace. Eight concurrent emissions on one fila exhaust the numbering retries (503).
        await Task.WhenAll(UnidadesCriadas.Select(u => Limitado(limite, async () =>
        {
            for (var n = 0; n < fichasPorUnidade; n++)
            {
                await EmitirAsync(u, n);
            }

            return u;
        })));
        await EnvelhecerFichasAsync();
    }

    /// <summary>Emits one ficha on the unidade through the totem chosen by <paramref name="sequencia"/>.</summary>
    public async Task<FichaEmitidaDto> EmitirAsync(UnidadeCarga unidade, int sequencia)
    {
        var totem = Totens.Where(t => t.UnidadeId == unidade.Id).ElementAt(sequencia % TotensPorUnidade);
        using var cliente = Cliente(totem.Token);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/paf/estacao/emissao/fichas");
        request.Content = JsonContent.Create(new EmitirFichaRequest(unidade.FilaId, unidade.PrioridadeId));
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        using var resposta = await cliente.SendAsync(request);
        resposta.StatusCode.ShouldBe(HttpStatusCode.OK, await resposta.Content.ReadAsStringAsync());
        return (await resposta.Content.ReadFromJsonAsync<FichaEmitidaDto>())!;
    }

    public HttpClient Cliente(string accessToken) => Cliente(host, accessToken, TenantId);

    private static async Task<T> Limitado<T>(SemaphoreSlim limite, Func<Task<T>> acao)
    {
        await limite.WaitAsync();
        try
        {
            return await acao();
        }
        finally
        {
            limite.Release();
        }
    }

    private static HttpClient Cliente(HostLatencia host, string accessToken, string tenant)
    {
        var client = host.Cliente();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        client.DefaultRequestHeaders.Add(MultitenancyConstants.Identifier, tenant);
        return client;
    }

    private async Task CriarTenantAsync()
    {
        using var root = Cliente(host, await EntrarAsync(TestConstants.RootTenantId, TestConstants.RootAdminEmail, TestConstants.DefaultPassword), TestConstants.RootTenantId);
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
        Admin = Cliente(host, await EntrarAsync(TenantId, adminEmail, TestConstants.DefaultPassword), TenantId);

        using var scope = host.Services.CreateScope();
        _tenant = await scope.ServiceProvider.GetRequiredService<IMultiTenantStore<AppTenantInfo>>().GetAsync(TenantId);
        _tenant.ShouldNotBeNull();
    }

    // Synchronous on purpose: the Finbuckle context is an AsyncLocal that would not flow back from an async helper.
    private PafDbContext Contexto(IServiceScope scope, Guid? unidadeCorrente)
    {
        scope.ServiceProvider.GetRequiredService<IMultiTenantContextSetter>().MultiTenantContext =
            new MultiTenantContext<AppTenantInfo>(_tenant ?? throw new InvalidOperationException("CriarTenantAsync não foi chamado."));
        if (unidadeCorrente is { } unidadeId)
        {
            scope.ServiceProvider.GetRequiredService<IUnidadeCorrente>().Definir(unidadeId);
        }

        return scope.ServiceProvider.GetRequiredService<PafDbContext>();
    }

    private async Task<UnidadeCarga> CriarUnidadeAsync(int indice)
    {
        using var scope = host.Services.CreateScope();
        var db = Contexto(scope, unidadeCorrente: null);
        var unidade = Unidade.Criar($"Loja {indice + 1:D2}", documentoFiscal: null);
        var u = unidade.Id;
        var caixa = Fila.Criar(u, "Caixa", "Caixa", setorId: null);
        var normal = Prioridade.Criar(u, "Normal", valor: 0);
        var guiches = Enumerable.Range(1, GuichesPorUnidade).Select(n => Estacao.Criar(u, $"Guichê {n}", TipoEstacao.Guiche)).ToList();
        var totens = Enumerable.Range(1, TotensPorUnidade).Select(n => Estacao.Criar(u, $"Totem {n}", TipoEstacao.Emissao)).ToList();
        var paineis = Enumerable.Range(1, PaineisPorUnidade).Select(n => Estacao.Criar(u, $"Painel {n}", TipoEstacao.Painel)).ToList();

        db.Unidades.Add(unidade);
        db.Filas.Add(caixa);
        db.Prioridades.Add(normal);
        db.Estacoes.AddRange(guiches.Concat(totens).Concat(paineis));
        db.FilaPrioridades.Add(FilaPrioridade.Criar(u, caixa.Id, normal.Id, "A", numeroInicial: 1, ordem: 0));
        db.EstacaoFilas.AddRange(guiches.Select(g => EstacaoFila.Criar(u, g.Id, PapelEstacao.Guiche, caixa.Id, 0)));
        db.EstacaoFilas.AddRange(totens.Select(t => EstacaoFila.Criar(u, t.Id, PapelEstacao.Emissao, caixa.Id, 0)));
        db.EstacaoFilas.AddRange(paineis.Select(p => EstacaoFila.Criar(u, p.Id, PapelEstacao.Painel, caixa.Id, 0)));
        db.EstacaoPrioridades.AddRange(guiches.Select(g => EstacaoPrioridade.Criar(u, g.Id, normal.Id, 0)));
        // One licence per guichê; started two days ago so the America/Sao_Paulo day is inside the validity at any UTC hour.
        db.ContratosUnidade.Add(ContratoUnidade.Criar(u, DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-2)), null, "Carga D20", GuichesPorUnidade, PoliticaLimiteLicenca.Recusar));
        await db.SaveChangesAsync();
        return new UnidadeCarga(u, caixa.Id, normal.Id, [.. guiches.Select(g => g.Id)], [.. totens.Select(t => t.Id)], [.. paineis.Select(p => p.Id)]);
    }

    private async Task<(string Papel, EstacaoCarga Estacao)> CredencialAsync(Guid unidadeId, Guid estacaoId, string papel)
    {
        VinculoEstacaoGeradoDto link;
        using (var scope = host.Services.CreateScope())
        {
            Contexto(scope, unidadeId);
            link = await scope.ServiceProvider.GetRequiredService<IMediator>().Send(new GerarVinculoEstacaoCommand(estacaoId));
        }

        using var client = host.Cliente();
        client.DefaultRequestHeaders.Add(MultitenancyConstants.Identifier, TenantId);
        using var resgate = await client.PostAsJsonAsync("/api/v1/paf/estacoes/vinculo/resgatar", new { token = link.Token });
        resgate.StatusCode.ShouldBe(HttpStatusCode.OK, await resgate.Content.ReadAsStringAsync());
        var credenciais = await resgate.Content.ReadFromJsonAsync<CredenciaisEstacaoDto>();
        return (papel, new EstacaoCarga(unidadeId, estacaoId, credenciais!.Credenciais.Single(c => c.Papel == papel).AccessToken));
    }

    private async Task<AtendenteCarga> AtendenteAsync(Guid unidadeId, Guid guiche)
    {
        var sufixo = Guid.NewGuid().ToString("N")[..8];
        var email = $"paf-lat-{sufixo}@example.com";
        const string senha = "Test@1234!";
        using var registro = await Admin.PostAsJsonAsync($"{TestConstants.IdentityBasePath}/register", new
        {
            firstName = "Atendente",
            lastName = "Carga",
            email,
            userName = $"paf-lat-{sufixo}",
            password = senha,
            confirmPassword = senha,
        });
        registro.StatusCode.ShouldBe(HttpStatusCode.Created, await registro.Content.ReadAsStringAsync());
        var usuarioId = (await registro.Content.ReadFromJsonAsync<RegisterResult>())!.UserId;

        using (var scope = host.Services.CreateScope())
        {
            var db = Contexto(scope, unidadeCorrente: null);
            var usuarios = scope.ServiceProvider.GetRequiredService<UserManager<FshUser>>();
            var usuario = await usuarios.FindByIdAsync(usuarioId);
            usuario.ShouldNotBeNull();
            (await usuarios.ConfirmEmailAsync(usuario, await usuarios.GenerateEmailConfirmationTokenAsync(usuario))).Succeeded.ShouldBeTrue();
            db.UsuariosUnidade.Add(UsuarioUnidade.Criar(Guid.Parse(usuarioId), unidadeId, 0));
            await db.SaveChangesAsync();
        }

        var token = await EntrarAsync(TenantId, email, senha);
        var cliente = Cliente(token);
        using var selecao = await cliente.PostAsJsonAsync("/api/v1/paf/sessao/guiche", new { estacaoId = guiche });
        selecao.StatusCode.ShouldBe(HttpStatusCode.NoContent, await selecao.Content.ReadAsStringAsync());
        return new AtendenteCarga(unidadeId, guiche, token, cliente);
    }

    /// <summary>Moves every ficha of the tenant past the five-second call guard (seção 4) without sleeping.</summary>
    private async Task EnvelhecerFichasAsync()
    {
        using var scope = host.Services.CreateScope();
        var db = Contexto(scope, unidadeCorrente: null);
        (await db.Database.ExecuteSqlAsync(
            $"""UPDATE paf."Fichas" SET "CriadoEm" = "CriadoEm" - interval '6 seconds' WHERE "TenantId" = {TenantId}""")).ShouldBeGreaterThan(0);
    }

    private async Task<string> EntrarAsync(string tenant, string email, string senha)
    {
        using var client = host.Cliente();
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{TestConstants.IdentityBasePath}/token/issue");
        request.Headers.Add(MultitenancyConstants.Identifier, tenant);
        request.Content = JsonContent.Create(new { email, password = senha });
        using var resposta = await client.SendAsync(request);
        resposta.StatusCode.ShouldBe(HttpStatusCode.OK, await resposta.Content.ReadAsStringAsync());
        return (await resposta.Content.ReadFromJsonAsync<TokenResult>())!.AccessToken;
    }
}
