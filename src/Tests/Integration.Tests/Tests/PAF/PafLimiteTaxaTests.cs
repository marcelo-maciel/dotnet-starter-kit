using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Finbuckle.MultiTenant;
using Finbuckle.MultiTenant.Abstractions;
using FSH.Framework.Shared.Multitenancy;
using FSH.Modules.Identity.Data;
using FSH.Modules.Identity.Domain;
using FSH.Modules.PAF.Contracts.Authorization;
using FSH.Modules.PAF.Contracts.v1.Estacoes;
using FSH.Modules.PAF.Data;
using FSH.Modules.PAF.Domain;
using FSH.Modules.PAF.Escopo;
using Integration.Tests.Infrastructure;
using Integration.Tests.Tests.Sessions;
using Mediator;
using Microsoft.AspNetCore.Identity;
using Xunit.Abstractions;

namespace Integration.Tests.Tests.PAF;

/// <summary>
/// Limite de taxa e proxy confiável com a configuração de produção (O12, ARQ l.380 e l.382, volume D20 da REQ
/// l.1072). O Caddy chega pela rede do Docker (<see cref="PafHostLimiteTaxa.ProxyConfiavel"/>) e repassa o IP da loja
/// em <c>X-Forwarded-For</c>; a loja é um IP de NAT com todas as suas estações atrás. Cada teste usa IPs e tenant
/// próprios, porque as janelas de 60 s do limitador vivem no host da coleção inteira.
/// </summary>
[Collection(PafHostLimiteTaxaColecao.Nome)]
public sealed class PafLimiteTaxaTests(PafHostLimiteTaxa host, ITestOutputHelper saida) : IAsyncLifetime
{
    // Measured on 19/10/2026 (tests/e2e/paf-abertura-loja.medicao.mjs, deploy/paf/README.md "Limite de taxa"):
    // requests of one station in its opening minute, every route of the screen included (kit shell, hub upgrades).
    private const int AberturaGuiche = 30;
    private const int AberturaTotem = 5;
    private const int AberturaPainel = 6;

    // D20 (REQ l.1072): per unidade 3 totens, 2 painéis, 8 guichês; 20 unidades in the reference client.
    private const int TotensPorLoja = 3;
    private const int PaineisPorLoja = 2;
    private const int GuichesPorLoja = 8;
    private const int LojasD20 = 20;

    // Peak assumption: a totem prints at most one ficha every 6 s, and each ficha costs its guichê three requests
    // (chamar, iniciar, encerrar) in the same minute.
    private const int FichasPorTotemNoPico = 10;
    private const int OperacoesPorFicha = 3;

    private const int PiorMinutoDaLoja =
        GuichesPorLoja * AberturaGuiche + TotensPorLoja * AberturaTotem + PaineisPorLoja * AberturaPainel
        + TotensPorLoja * FichasPorTotemNoPico * (1 + OperacoesPorFicha);

    // Acceptance criterion of O12: a store with more than 10 attendants behind one NAT opens without a refusal.
    private const int LoginsDaAbertura = 15;

    private static readonly TimeSpan Janela = TimeSpan.FromSeconds(60);

    private readonly string _tenantId = $"paf-lt-{Guid.NewGuid().ToString("N")[..8]}";

    // Every test of the process gets its own /16 of the shared address space (RFC 6598) for its stores and its
    // own setup address: the limiter's 60 s windows outlive the test, so two tests never share a partition.
    private static int _ultimoPrefixo = 63;
    private readonly int _prefixo = Interlocked.Increment(ref _ultimoPrefixo);

    private string PrefixoLojas => $"100.{_prefixo}";

    // Setup traffic comes from its own address, outside every store and outside the Docker network.
    private string IpPreparacao => $"198.18.{_prefixo}.1";

    private AppTenantInfo _tenant = default!;
    private HttpClient _admin = default!;

    public async Task InitializeAsync()
    {
        using var root = await ClienteRootAsync();
        var adminEmail = $"admin-{_tenantId}@paf.test";
        using var criar = await root.PostAsJsonAsync(TestConstants.TenantsBasePath, new
        {
            id = _tenantId,
            name = $"Tenant {_tenantId}",
            connectionString = (string?)null,
            adminEmail,
            adminPassword = TestConstants.DefaultPassword,
            issuer = $"{_tenantId}.issuer",
        });
        criar.StatusCode.ShouldBe(HttpStatusCode.Created, await criar.Content.ReadAsStringAsync());
        await TenantProvisioningWait.WaitForProvisioningAsync(root, _tenantId);
        _admin = ClientePreparacao((await TokenAsync(_tenantId, adminEmail, TestConstants.DefaultPassword)).AccessToken, _tenantId);

        using var scope = host.Services.CreateScope();
        _tenant = (await scope.ServiceProvider.GetRequiredService<IMultiTenantStore<AppTenantInfo>>().GetAsync(_tenantId))!;
        _tenant.ShouldNotBeNull();
    }

    public Task DisposeAsync()
    {
        _admin?.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public void LimitesDoArquivoDeProducao_CobremOPiorMinutoMedidoComOVolumeD20()
    {
        var limites = host.Limites;
        saida.WriteLine($"pior minuto: loja {PiorMinutoDaLoja}, tenant {PiorMinutoDaLoja * LojasD20}; limites Ip {limites.Ip.PermitLimit}, Tenant {limites.Tenant.PermitLimit}, Auth {limites.Auth.PermitLimit}, User {limites.User.PermitLimit}");

        limites.Enabled.ShouldBeTrue();
        limites.Ip.PermitLimit.ShouldBeGreaterThanOrEqualTo(PiorMinutoDaLoja);
        limites.Tenant.PermitLimit.ShouldBeGreaterThanOrEqualTo(PiorMinutoDaLoja * LojasD20);
        limites.Auth.PermitLimit.ShouldBeGreaterThanOrEqualTo(LoginsDaAbertura);
        // The busiest single identity is one guichê: its opening plus its share of the peak operations.
        limites.User.PermitLimit.ShouldBeGreaterThanOrEqualTo(AberturaGuiche + TotensPorLoja * FichasPorTotemNoPico * OperacoesPorFicha);
        foreach (var particao in new[] { limites.Ip, limites.Tenant, limites.Auth, limites.User })
        {
            particao.WindowSeconds.ShouldBe((int)Janela.TotalSeconds);
        }
    }

    [Fact]
    public async Task Login_QuinzeAtendentesDaMesmaLojaAtrasDoCaddyEm60s_TodosOk_EOIpAuditadoEODaLoja()
    {
        var unidade = await UnidadeAsync(licencas: LoginsDaAbertura + 5);
        var atendentes = new List<SeededUser>();
        for (var i = 0; i < LoginsDaAbertura; i++)
        {
            atendentes.Add(await UsuarioAsync(unidade.UnidadeId));
        }

        var ipLoja = IpLoja(1);
        var relogio = Stopwatch.StartNew();
        var status = new List<HttpStatusCode>();
        foreach (var atendente in atendentes)
        {
            using var resposta = await LoginAsync(atendente.Email, atendente.Password, IpCaddy(), ipLoja);
            status.Add(resposta.StatusCode);
        }

        relogio.Elapsed.ShouldBeLessThan(Janela, "os 15 logins precisam cair na mesma janela para o teste valer");
        status.ShouldAllBe(s => s == HttpStatusCode.OK, string.Join(",", status));
        (await IpsDasSessoesAsync(atendentes.Select(a => a.UserId))).ShouldAllBe(ip => ip == ipLoja);
    }

    [Fact]
    public async Task XForwardedForForjadoDeRedeNaoConfiavel_NaoAlteraOIpAuditado_NemEscapaDaParticaoDoRemetente()
    {
        var unidade = await UnidadeAsync(licencas: 5);
        var usuario = await UsuarioAsync(unidade.UnidadeId);
        var forasteiro = $"{PrefixoLojas}.250.9";

        using (var forjado = await LoginAsync(usuario.Email, usuario.Password, forasteiro, IpLoja(7)))
        {
            forjado.StatusCode.ShouldBe(HttpStatusCode.OK, await forjado.Content.ReadAsStringAsync());
        }

        // Same header from Caddy: honoured. Without this arm the first one passes with forwarding off.
        using (var peloCaddy = await LoginAsync(usuario.Email, usuario.Password, IpCaddy(), IpLoja(7)))
        {
            peloCaddy.StatusCode.ShouldBe(HttpStatusCode.OK, await peloCaddy.Content.ReadAsStringAsync());
        }

        // ForwardLimit 1: only the hop Caddy appended is trusted, whatever the client wrote before it.
        using (var cadeia = await LoginAsync(usuario.Email, usuario.Password, IpCaddy(), $"{IpLoja(9)}, {IpLoja(8)}"))
        {
            cadeia.StatusCode.ShouldBe(HttpStatusCode.OK, await cadeia.Content.ReadAsStringAsync());
        }

        var ips = await IpsDasSessoesAsync([usuario.UserId]);
        ips.Order(StringComparer.Ordinal).ShouldBe(new[] { forasteiro, IpLoja(7), IpLoja(8) }.Order(StringComparer.Ordinal));

        // The auth partition follows the sender, not the forged header: a new forged address per request still
        // lands in one partition and the sender is refused once its window is spent (the first login above counted).
        var limite = host.Limites.Auth.PermitLimit;
        var respostas = new List<(HttpStatusCode Status, string? RetryAfter)>();
        for (var i = 0; i < limite; i++)
        {
            // An unknown e-mail, so the kit's lockout (423) never enters: every answer is 401 or the limiter's 429.
            using var resposta = await LoginAsync($"ninguem-{Guid.NewGuid():N}@example.com", "senha-errada", forasteiro, $"{PrefixoLojas}.251.{i % 250}");
            respostas.Add((resposta.StatusCode, resposta.Headers.RetryAfter?.ToString()));
        }

        respostas.ShouldAllBe(r => r.Status == HttpStatusCode.Unauthorized || r.Status == HttpStatusCode.TooManyRequests);
        var recusada = respostas[^1];
        recusada.Status.ShouldBe(HttpStatusCode.TooManyRequests, string.Join(",", respostas.Select(r => r.Status)));
        int.Parse(recusada.RetryAfter!, System.Globalization.CultureInfo.InvariantCulture).ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task PicoDaLojaEmMinutoCheio_OsTresTotensEmitemSemRecusa()
    {
        var unidade = await UnidadeAsync(licencas: 5);
        var totens = await TotensAsync(unidade, IpLoja(1));

        // The rest of the store's worst minute, through Caddy with the store's address: the opening of 8
        // guichês and 2 painéis, the guichê operations of every ficha the totens print, and the totens' own
        // opening besides the configuration read below. Anonymous requests count in the IP partition like any other.
        var restante = PiorMinutoDaLoja - TotensPorLoja * (1 + FichasPorTotemNoPico);
        var relogio = Stopwatch.StartNew();
        var preenchimento = await PreencherParticaoIpAsync(IpLoja(1), restante);
        preenchimento.ShouldAllBe(s => s == HttpStatusCode.Unauthorized, string.Join(",", preenchimento.Distinct()));

        var emissoes = await EmitirNoPicoAsync(unidade, totens);
        relogio.Elapsed.ShouldBeLessThan(Janela, "a loja inteira precisa caber numa janela para o teste valer");
        saida.WriteLine($"loja: {restante} + {emissoes.Count} requisições em {relogio.Elapsed.TotalSeconds:F1} s");

        emissoes.ShouldAllBe(s => s == HttpStatusCode.OK, string.Join(",", emissoes));
        (await ContarFichasAsync(unidade.UnidadeId)).ShouldBe(TotensPorLoja * FichasPorTotemNoPico);
    }

    [Fact]
    public async Task PicoDoTenant_SessentaTotensDasVinteLojasD20EmitemNoMesmoMinuto_SemRecusa()
    {
        var lojas = new List<(Loja Loja, List<HttpClient> Totens)>();
        for (var loja = 1; loja <= LojasD20; loja++)
        {
            var unidade = await UnidadeAsync(licencas: 5);
            lojas.Add((unidade, await TotensAsync(unidade, IpLoja(loja))));
        }

        var relogio = Stopwatch.StartNew();
        var porLoja = await Task.WhenAll(lojas.Select(l => EmitirNoPicoAsync(l.Loja, l.Totens)));
        relogio.Elapsed.ShouldBeLessThan(Janela, "as 20 lojas precisam caber numa janela para o teste valer");
        var status = porLoja.SelectMany(s => s).ToList();
        saida.WriteLine($"tenant: {status.Count} requisições de {LojasD20 * TotensPorLoja} totens em {relogio.Elapsed.TotalSeconds:F1} s");

        status.Count.ShouldBe(LojasD20 * TotensPorLoja * (1 + FichasPorTotemNoPico));
        status.ShouldAllBe(s => s == HttpStatusCode.OK, string.Join(",", status.Where(s => s != HttpStatusCode.OK).Distinct()));
        foreach (var (unidade, _) in lojas)
        {
            (await ContarFichasAsync(unidade.UnidadeId)).ShouldBe(TotensPorLoja * FichasPorTotemNoPico);
        }
    }

    [Fact]
    public async Task AcimaDoLimiteDaLoja_Emissao429ComRetryAfter_SemFichaESem500()
    {
        var unidade = await UnidadeAsync(licencas: 5);
        var totem = (await TotensAsync(unidade, IpLoja(3)))[0];

        var preenchimento = await PreencherParticaoIpAsync(IpLoja(3), host.Limites.Ip.PermitLimit);
        preenchimento.ShouldAllBe(s => s == HttpStatusCode.Unauthorized || s == HttpStatusCode.TooManyRequests);

        using var recusada = await EmitirAsync(totem, unidade, Guid.NewGuid().ToString("N"));
        var corpo = await recusada.Content.ReadAsStringAsync();
        recusada.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests, corpo);
        recusada.Headers.RetryAfter.ShouldNotBeNull("o totem precisa saber quando tentar de novo");
        recusada.Headers.RetryAfter.Delta!.Value.ShouldBeGreaterThan(TimeSpan.Zero);
        recusada.Headers.RetryAfter.Delta.Value.ShouldBeLessThanOrEqualTo(Janela);
        // The kit's OnRejected body is a ProblemDetails; its media type ends up application/json because
        // WriteAsJsonAsync overwrites the problem+json set just before it (RateLimiting/Extensions.cs, kit, untouched).
        using (var problema = JsonDocument.Parse(corpo))
        {
            problema.RootElement.GetProperty("status").GetInt32().ShouldBe((int)HttpStatusCode.TooManyRequests);
        }

        (await ContarFichasAsync(unidade.UnidadeId)).ShouldBe(0);

        // Another store of the same tenant keeps emitting: the refusal is the store's partition, not the tenant's.
        var outra = await UnidadeAsync(licencas: 5);
        var totemOutra = (await TotensAsync(outra, IpLoja(4)))[0];
        using var aceita = await EmitirAsync(totemOutra, outra, Guid.NewGuid().ToString("N"));
        aceita.StatusCode.ShouldBe(HttpStatusCode.OK, await aceita.Content.ReadAsStringAsync());
    }

    // ─── loja e estações ─────────────────────────────────────────────

    public sealed record Loja(Guid UnidadeId, Guid Fila, Guid Prioridade, IReadOnlyList<Guid> Totens);

    private string IpLoja(int loja) => $"{PrefixoLojas}.{loja / 250}.{(loja % 250) + 1}";

    // Caddy's fixed address in the Docker network, the only proxy the API trusts (O03).
    private string IpCaddy() => host.ProxyConfiavel;

    private async Task<Loja> UnidadeAsync(int licencas)
    {
        using var scope = host.Services.CreateScope();
        var db = Contexto(scope, unidadeCorrente: null);
        var unidade = FSH.Modules.PAF.Domain.Unidade.Criar($"Loja {Guid.NewGuid():N}", documentoFiscal: null);
        var u = unidade.Id;
        var fila = Fila.Criar(u, "Caixa", "Caixa", setorId: null);
        var normal = Prioridade.Criar(u, "Normal", valor: 0);
        var totens = Enumerable.Range(1, TotensPorLoja).Select(i => Estacao.Criar(u, $"Totem {i}", TipoEstacao.Emissao)).ToList();
        // Emission needs a guichê covering the fila and the prioridade (C07).
        var guiche = Estacao.Criar(u, "Guichê 1", TipoEstacao.Guiche);
        db.Unidades.Add(unidade);
        db.Filas.Add(fila);
        db.Prioridades.Add(normal);
        db.Estacoes.AddRange(totens);
        db.Estacoes.Add(guiche);
        db.FilaPrioridades.Add(FilaPrioridade.Criar(u, fila.Id, normal.Id, "A", numeroInicial: 1, ordem: 0));
        db.EstacaoFilas.AddRange(totens.Select(t => EstacaoFila.Criar(u, t.Id, PapelEstacao.Emissao, fila.Id, 0)));
        db.EstacaoFilas.Add(EstacaoFila.Criar(u, guiche.Id, PapelEstacao.Guiche, fila.Id, 0));
        db.EstacaoPrioridades.Add(EstacaoPrioridade.Criar(u, guiche.Id, normal.Id, 0));
        // Started two days ago so the America/Sao_Paulo calendar day is inside the validity at any UTC hour.
        db.ContratosUnidade.Add(ContratoUnidade.Criar(u, DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-2)), null, "Teste", licencas, PoliticaLimiteLicenca.Recusar));
        await db.SaveChangesAsync();
        return new Loja(u, fila.Id, normal.Id, totens.Select(t => t.Id).ToList());
    }

    /// <summary>Each totem of the store redeems its link from the store's address, through Caddy.</summary>
    private async Task<List<HttpClient>> TotensAsync(Loja unidade, string ipLoja)
    {
        var clientes = new List<HttpClient>();
        foreach (var totem in unidade.Totens)
        {
            VinculoEstacaoGeradoDto link;
            using (var scope = host.Services.CreateScope())
            {
                Contexto(scope, unidade.UnidadeId);
                link = await scope.ServiceProvider.GetRequiredService<IMediator>().Send(new GerarVinculoEstacaoCommand(totem));
            }

            using var anonimo = ClienteDaLoja(ipLoja);
            anonimo.DefaultRequestHeaders.Add("tenant", _tenantId);
            using var resgate = await anonimo.PostAsJsonAsync("/api/v1/paf/estacoes/vinculo/resgatar", new { token = link.Token });
            resgate.StatusCode.ShouldBe(HttpStatusCode.OK, await resgate.Content.ReadAsStringAsync());
            var credencial = (await resgate.Content.ReadFromJsonAsync<CredenciaisEstacaoDto>())!.Credenciais.Single(c => c.Papel == PafPapeisEstacao.Emissao);

            var cliente = ClienteDaLoja(ipLoja);
            cliente.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", credencial.AccessToken);
            cliente.DefaultRequestHeaders.Add("tenant", _tenantId);
            clientes.Add(cliente);
        }

        return clientes;
    }

    /// <summary>
    /// The store's peak: every totem opens (configuration read) and prints its fichas. Sequential inside the store,
    /// because concurrent emissions on one fila exhaust the numbering retries (O17 finding, out of O12's scope).
    /// </summary>
    private static async Task<List<HttpStatusCode>> EmitirNoPicoAsync(Loja unidade, List<HttpClient> totens)
    {
        var status = new List<HttpStatusCode>();
        foreach (var totem in totens)
        {
            using var configuracao = await totem.GetAsync("/api/v1/paf/estacao/emissao/configuracao");
            status.Add(configuracao.StatusCode);
        }

        for (var i = 0; i < FichasPorTotemNoPico; i++)
        {
            foreach (var totem in totens)
            {
                using var resposta = await EmitirAsync(totem, unidade, Guid.NewGuid().ToString("N"));
                status.Add(resposta.StatusCode);
            }
        }

        return status;
    }

    private static Task<HttpResponseMessage> EmitirAsync(HttpClient totem, Loja unidade, string chave) =>
        PafOperacao.EmitirAsync(totem, unidade.Fila, unidade.Prioridade, chave);

    /// <summary>Spends <paramref name="quantidade"/> of the store's IP window with anonymous requests (401).</summary>
    private async Task<List<HttpStatusCode>> PreencherParticaoIpAsync(string ipLoja, int quantidade)
    {
        using var cliente = ClienteDaLoja(ipLoja);
        var status = new List<HttpStatusCode>();
        foreach (var lote in Enumerable.Range(0, quantidade).Chunk(25))
        {
            var respostas = await Task.WhenAll(lote.Select(_ => cliente.GetAsync($"{TestConstants.IdentityBasePath}/profile")));
            foreach (var resposta in respostas)
            {
                status.Add(resposta.StatusCode);
                resposta.Dispose();
            }
        }

        return status;
    }

    private async Task<int> ContarFichasAsync(Guid unidadeId)
    {
        using var scope = host.Services.CreateScope();
        return await Contexto(scope, unidadeId).Fichas.CountAsync();
    }

    private PafDbContext Contexto(IServiceScope scope, Guid? unidadeCorrente)
    {
        scope.ServiceProvider.GetRequiredService<IMultiTenantContextSetter>().MultiTenantContext = new MultiTenantContext<AppTenantInfo>(_tenant);
        if (unidadeCorrente is { } unidadeId)
        {
            scope.ServiceProvider.GetRequiredService<IUnidadeCorrente>().Definir(unidadeId);
        }

        return scope.ServiceProvider.GetRequiredService<PafDbContext>();
    }

    // ─── pessoas ─────────────────────────────────────────────────────

    /// <summary>A client whose connection is Caddy and whose original address is the store's NAT.</summary>
    private HttpClient ClienteDaLoja(string ipLoja)
    {
        var cliente = host.CreateClient();
        cliente.DefaultRequestHeaders.Add(TestRemoteIpStartupFilter.RemoteIpHeader, IpCaddy());
        cliente.DefaultRequestHeaders.Add("X-Forwarded-For", ipLoja);
        return cliente;
    }

    private HttpClient ClientePreparacao(string accessToken, string tenant)
    {
        var cliente = host.CreateClient();
        cliente.DefaultRequestHeaders.Add(TestRemoteIpStartupFilter.RemoteIpHeader, IpPreparacao);
        cliente.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        cliente.DefaultRequestHeaders.Add("tenant", tenant);
        return cliente;
    }

    private async Task<HttpClient> ClienteRootAsync()
    {
        var token = await TokenAsync(TestConstants.RootTenantId, TestConstants.RootAdminEmail, TestConstants.DefaultPassword);
        return ClientePreparacao(token.AccessToken, TestConstants.RootTenantId);
    }

    private async Task<HttpResponseMessage> LoginAsync(string email, string senha, string ipConexao, string encaminhadoPara)
    {
        using var cliente = host.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{TestConstants.IdentityBasePath}/token/issue");
        request.Headers.Add("tenant", _tenantId);
        request.Headers.Add(TestRemoteIpStartupFilter.RemoteIpHeader, ipConexao);
        request.Headers.Add("X-Forwarded-For", encaminhadoPara);
        request.Content = JsonContent.Create(new { email, password = senha });
        return await cliente.SendAsync(request);
    }

    private async Task<TokenResult> TokenAsync(string tenant, string email, string senha)
    {
        using var cliente = host.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{TestConstants.IdentityBasePath}/token/issue");
        request.Headers.Add("tenant", tenant);
        request.Headers.Add(TestRemoteIpStartupFilter.RemoteIpHeader, IpPreparacao);
        request.Content = JsonContent.Create(new { email, password = senha });
        using var resposta = await cliente.SendAsync(request);
        resposta.StatusCode.ShouldBe(HttpStatusCode.OK, await resposta.Content.ReadAsStringAsync());
        return (await resposta.Content.ReadFromJsonAsync<TokenResult>())!;
    }

    private async Task<List<string>> IpsDasSessoesAsync(IEnumerable<string> usuarios)
    {
        var ids = usuarios.ToList();
        using var scope = host.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<IMultiTenantContextSetter>().MultiTenantContext = new MultiTenantContext<AppTenantInfo>(_tenant);
        return await scope.ServiceProvider.GetRequiredService<IdentityDbContext>().UserSessions.AsNoTracking()
            .Where(s => ids.Contains(s.UserId))
            .Select(s => s.IpAddress)
            .ToListAsync();
    }

    /// <summary>Usuário com login confirmado e escopo só na unidade dada.</summary>
    private async Task<SeededUser> UsuarioAsync(Guid unidadeId)
    {
        var id = Guid.NewGuid().ToString("N")[..8];
        var email = $"paf-lt-{id}@example.com";
        const string senha = "Test@1234!";
        using var registro = await _admin.PostAsJsonAsync($"{TestConstants.IdentityBasePath}/register", new
        {
            firstName = "Seeded",
            lastName = "User",
            email,
            userName = $"paf-lt-{id}",
            password = senha,
            confirmPassword = senha,
        });
        registro.StatusCode.ShouldBe(HttpStatusCode.Created, await registro.Content.ReadAsStringAsync());
        var usuarioId = (await registro.Content.ReadFromJsonAsync<RegisterResult>())!.UserId;

        using var scope = host.Services.CreateScope();
        var db = Contexto(scope, unidadeCorrente: null);
        var usuarios = scope.ServiceProvider.GetRequiredService<UserManager<FshUser>>();
        var usuario = (await usuarios.FindByEmailAsync(email))!;
        (await usuarios.ConfirmEmailAsync(usuario, await usuarios.GenerateEmailConfirmationTokenAsync(usuario))).Succeeded.ShouldBeTrue();
        db.UsuariosUnidade.Add(UsuarioUnidade.Criar(Guid.Parse(usuarioId), unidadeId, 0));
        await db.SaveChangesAsync();
        return new SeededUser(usuarioId, email, senha);
    }
}
