using System.Diagnostics;
using System.IdentityModel.Tokens.Jwt;
using System.Text.Json;
using Finbuckle.MultiTenant;
using Finbuckle.MultiTenant.Abstractions;
using FSH.Framework.Caching;
using FSH.Framework.Shared.Multitenancy;
using FSH.Modules.Identity.Domain;
using FSH.Modules.PAF.Contracts.Authorization;
using FSH.Modules.PAF.Contracts.v1.Estacoes;
using FSH.Modules.PAF.Contracts.v1.Fichas;
using FSH.Modules.PAF.Data;
using FSH.Modules.PAF.Domain;
using FSH.Modules.PAF.Escopo;
using FSH.Modules.PAF.Saude;
using FSH.Modules.PAF.Sessao;
using Integration.Tests.Infrastructure;
using Integration.Tests.Tests.Sessions;
using Mediator;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Caching.Hybrid;
using StackExchange.Redis;
using Xunit.Abstractions;

namespace Integration.Tests.Tests.PAF;

/// <summary>
/// Modo degradado sem Valkey (S14, seção 9 da arquitetura, RNF05) pelo host real ligado a um Valkey pausável:
/// com o contêiner pausado a conexão continua aberta e cada chamada espera o prazo, que é o pior caso (um Valkey
/// parado de vez derruba a conexão e falha na hora). Emissão, guichê, login na última vaga, troca de unidade,
/// concessão de impersonação e a saúde própria do PAF.
/// </summary>
[Collection(PafHostValkeyColecao.Nome)]
public sealed class PafModoDegradadoTests(PafHostValkey host, ITestOutputHelper saida) : IAsyncLifetime
{
    // Ceiling of a guichê operation with the Valkey paused: the PAF's own Valkey calls stop at 500 ms and the
    // kit's at the connection string's 500 ms (PafHostValkey.PrazosValkey).
    private static readonly TimeSpan TetoOperacao = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan TetoSaudeSemBanco = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan EsperaReconstrucao = TimeSpan.FromSeconds(45);

    private readonly string _tenantId = $"paf-dg-{Guid.NewGuid().ToString("N")[..8]}";
    private AppTenantInfo _tenant = default!;
    private HttpClient _admin = default!;
    private bool _valkeyPausado;
    private bool _postgresPausado;

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
        _admin = Cliente((await TokenAsync(_tenantId, adminEmail, TestConstants.DefaultPassword)).AccessToken);

        using var scope = host.Services.CreateScope();
        _tenant = (await scope.ServiceProvider.GetRequiredService<IMultiTenantStore<AppTenantInfo>>().GetAsync(_tenantId))!;
        _tenant.ShouldNotBeNull();
    }

    // A failed assertion must not leave the shared containers paused for the next test.
    public async Task DisposeAsync()
    {
        if (_valkeyPausado)
        {
            await host.RetomarValkeyAsync();
        }

        if (_postgresPausado)
        {
            await host.RetomarPostgresAsync();
        }

        _admin?.Dispose();
    }

    [Fact]
    public async Task ValkeyPausado_TotemEmite_EReenvioComAMesmaChaveDevolveAMesmaFicha()
    {
        var c = await CenarioAsync(limite: 5);
        using var totem = Cliente(await CredencialTotemAsync(c));
        await PausarValkeyAsync();

        var chave = Guid.NewGuid().ToString("N");
        var relogio = Stopwatch.StartNew();
        using var primeira = await PafOperacao.EmitirAsync(totem, c.Fila, c.Prioridade, chave);
        var tempoPrimeira = relogio.Elapsed;
        primeira.StatusCode.ShouldBe(HttpStatusCode.OK, await primeira.Content.ReadAsStringAsync());
        var ficha = (await primeira.Content.ReadFromJsonAsync<FichaEmitidaDto>())!;

        using var reenvio = await PafOperacao.EmitirAsync(totem, c.Fila, c.Prioridade, chave);
        reenvio.StatusCode.ShouldBe(HttpStatusCode.OK, await reenvio.Content.ReadAsStringAsync());
        var repetida = (await reenvio.Content.ReadFromJsonAsync<FichaEmitidaDto>())!;
        saida.WriteLine($"emissão com Valkey pausado: {tempoPrimeira.TotalMilliseconds:F0} ms");

        // The kit's idempotency filter treats every key as new with the cache down; the ficha's key index answers.
        repetida.FichaId.ShouldBe(ficha.FichaId);
        repetida.Senha.ShouldBe(ficha.Senha);
        (await ContarFichasAsync(c)).ShouldBe(1);
        tempoPrimeira.ShouldBeLessThan(TetoOperacao);
    }

    [Fact]
    public async Task ValkeyPausado_GuicheChamaIniciaEEncerra_P95AbaixoDoTeto()
    {
        const int Ciclos = 8;
        var c = await CenarioAsync(limite: 5);
        using var totem = Cliente(await CredencialTotemAsync(c));
        for (var i = 0; i < Ciclos; i++)
        {
            await PafOperacao.EmitirOkAsync(totem, c.Fila, c.Prioridade);
        }

        await EnvelhecerFichasAsync(c);
        using var atendente = await AtendenteComGuicheAsync(c);
        await PausarValkeyAsync();

        var tempos = new List<TimeSpan>();
        for (var i = 0; i < Ciclos; i++)
        {
            var relogio = Stopwatch.StartNew();
            var chamada = await PafOperacao.ChamarOkAsync(atendente);
            tempos.Add(relogio.Elapsed);
            relogio.Restart();
            await PafOperacao.AgirOkAsync(atendente, chamada.AtendimentoId, "iniciar");
            tempos.Add(relogio.Elapsed);
            relogio.Restart();
            (await PafOperacao.AgirOkAsync(atendente, chamada.AtendimentoId, "encerrar")).Estado.ShouldBe(nameof(EstadoAtendimento.Encerrado));
            tempos.Add(relogio.Elapsed);
        }

        var ordenados = tempos.OrderBy(t => t).ToList();
        var p95 = ordenados[(int)Math.Ceiling(ordenados.Count * 0.95) - 1];
        saida.WriteLine($"guichê com Valkey pausado: p50 {ordenados[ordenados.Count / 2].TotalMilliseconds:F0} ms, p95 {p95.TotalMilliseconds:F0} ms, máx {ordenados[^1].TotalMilliseconds:F0} ms");
        p95.ShouldBeLessThan(TetoOperacao);
    }

    [Fact]
    public async Task ValkeyPausado_DoisLoginsNaUltimaVaga_UmAceito_ERetomadoOConjuntoReconstruidoTemAMesmaOcupacao()
    {
        var c = await CenarioAsync(limite: 2);
        var ocupante = await UsuarioAsync(c.UnidadeId);
        await TokenAsync(_tenantId, ocupante.Email, ocupante.Password);
        var a = await UsuarioAsync(c.UnidadeId);
        var b = await UsuarioAsync(c.UnidadeId);
        await PausarValkeyAsync();

        var respostas = await Task.WhenAll(LoginAsync(a), LoginAsync(b));
        var corpos = await Task.WhenAll(respostas.Select(r => r.Content.ReadAsStringAsync()));
        respostas.Count(r => r.StatusCode == HttpStatusCode.OK).ShouldBe(1, string.Join(" | ", corpos));
        respostas.Count(r => r.StatusCode == HttpStatusCode.Forbidden).ShouldBe(1, string.Join(" | ", corpos));
        foreach (var r in respostas)
        {
            r.Dispose();
        }

        var vivas = await SessoesVivasAsync(c.UnidadeId);
        vivas.Count.ShouldBe(2, "a contagem do PostgreSQL decide a última vaga");

        await RetomarValkeyAsync();
        var membros = await EsperarAsync(
            () => MembrosAsync(c.UnidadeId),
            m => m.Order().SequenceEqual(vivas.Select(ConjuntoLicencas.Membro).Order()),
            EsperaReconstrucao);
        membros.Order().ShouldBe(vivas.Select(ConjuntoLicencas.Membro).Order(), "a volta do Valkey reconstrói o conjunto pelo PostgreSQL");
    }

    [Fact]
    public async Task ValkeyPausado_TrocaDeUnidade503_EComValkeyDeVoltaATrocaPassa()
    {
        var c = await CenarioAsync(limite: 5);
        var outra = await CenarioAsync(limite: 5);
        var usuario = await UsuarioAsync(c.UnidadeId, outra.UnidadeId);
        using var cliente = Cliente((await TokenAsync(_tenantId, usuario.Email, usuario.Password)).AccessToken);
        await PausarValkeyAsync();

        var relogio = Stopwatch.StartNew();
        using (var troca = await cliente.PostAsJsonAsync("/api/v1/paf/sessao/unidade-corrente", new { unidadeId = outra.UnidadeId }))
        {
            troca.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable, await troca.Content.ReadAsStringAsync());
        }

        relogio.Elapsed.ShouldBeLessThan(TetoOperacao);
        await RetomarValkeyAsync();

        var depois = await EsperarAsync(
            async () =>
            {
                using var troca = await cliente.PostAsJsonAsync("/api/v1/paf/sessao/unidade-corrente", new { unidadeId = outra.UnidadeId });
                return troca.StatusCode;
            },
            s => s == HttpStatusCode.NoContent,
            EsperaReconstrucao);
        depois.ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task ValkeyPausado_ConcessaoDaCodefiConferidaNoBanco_SemCacheDaConcessao()
    {
        var c = await CenarioAsync(limite: 5);
        var alvo = await UsuarioAsync(c.UnidadeId);
        await DarPapelAsync(alvo.UserId, PafPermissions.Guiches.Selecionar);
        using var root = await ClienteRootAsync();
        string token;
        using (var inicio = await root.PostAsJsonAsync($"{TestConstants.IdentityBasePath}/impersonation/start", new
        {
            targetUserId = alvo.UserId,
            targetTenantId = _tenantId,
            reason = "S14 modo degradado",
        }))
        {
            inicio.StatusCode.ShouldBe(HttpStatusCode.OK, await inicio.Content.ReadAsStringAsync());
            using var json = JsonDocument.Parse(await inicio.Content.ReadAsStringAsync());
            token = json.RootElement.GetProperty("accessToken").GetString()!;
        }

        // Drop the grant status from both cache levels while the Valkey is up, so the check below can only come
        // from the L2 (paused) and then the database.
        var jti = new JwtSecurityTokenHandler().ReadJwtToken(token).Id;
        using (var scope = host.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<HybridCache>().RemoveAsync(CacheKeys.ImpersonationGrantStatus(jti));
        }

        using var suporte = Cliente(token);
        await PausarValkeyAsync();
        var relogio = Stopwatch.StartNew();
        using var unidades = await suporte.GetAsync("/api/v1/paf/guiche/unidades");
        saida.WriteLine($"concessão sem cache com Valkey pausado: {relogio.Elapsed.TotalMilliseconds:F0} ms");
        unidades.StatusCode.ShouldBe(HttpStatusCode.OK, await unidades.Content.ReadAsStringAsync());
        (await unidades.Content.ReadAsStringAsync()).ShouldContain(c.UnidadeId.ToString());
        relogio.Elapsed.ShouldBeLessThan(TetoOperacao);
    }

    [Fact]
    public async Task Saude_ValkeyPausadoDegraded200EReady503_PostgresPausado503RapidoEVoltaAHealthy()
    {
        using var anonimo = host.CreateClient();
        (await SaudeAsync(anonimo)).Status.ShouldBe(HttpStatusCode.OK);
        (await SaudeAsync(anonimo)).Corpo.Status.ShouldBe(nameof(Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Healthy));

        await PausarValkeyAsync();
        var degradada = await SaudeAsync(anonimo);
        degradada.Status.ShouldBe(HttpStatusCode.OK);
        degradada.Corpo.Status.ShouldBe("Degraded");
        degradada.Corpo.Itens.Single(i => i.Nome == SaudePaf.NomeValkey).Status.ShouldBe("Degraded");
        using (var pronto = await anonimo.GetAsync("/health/ready"))
        {
            pronto.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable, "o /health/ready do kit não conhece o modo degradado");
        }

        await RetomarValkeyAsync();
        (await EsperarAsync(() => SaudeAsync(anonimo), s => s.Corpo.Status == "Healthy", EsperaReconstrucao)).Status.ShouldBe(HttpStatusCode.OK);

        await host.PausarPostgresAsync();
        _postgresPausado = true;
        var relogio = Stopwatch.StartNew();
        var semBanco = await SaudeAsync(anonimo);
        var tempo = relogio.Elapsed;
        saida.WriteLine($"paf/saude com PostgreSQL pausado: {tempo.TotalMilliseconds:F0} ms");
        semBanco.Status.ShouldBe(HttpStatusCode.ServiceUnavailable);
        semBanco.Corpo.Itens.Single(i => i.Nome == SaudePaf.NomePostgres).Status.ShouldBe("Unhealthy");
        tempo.ShouldBeLessThan(TetoSaudeSemBanco);

        await host.RetomarPostgresAsync();
        _postgresPausado = false;
        var devolta = await EsperarAsync(() => SaudeAsync(anonimo), s => s.Corpo.Status == "Healthy", TimeSpan.FromSeconds(20));
        devolta.Status.ShouldBe(HttpStatusCode.OK, "volta a Healthy sem reiniciar o host");
    }

    // ─── cenário ─────────────────────────────────────────────────────

    private sealed record Cenario(Guid UnidadeId, Guid Fila, Guid Prioridade, Guid Guiche, Guid Totem);

    private sealed record Saude(HttpStatusCode Status, SaudePafDto Corpo);

    private async Task PausarValkeyAsync()
    {
        await host.PausarValkeyAsync();
        _valkeyPausado = true;
    }

    private async Task RetomarValkeyAsync()
    {
        await host.RetomarValkeyAsync();
        _valkeyPausado = false;
    }

    private async Task<Saude> SaudeAsync(HttpClient cliente)
    {
        using var resposta = await cliente.GetAsync($"/api/v1/paf{SaudePaf.Rota}");
        var corpo = await resposta.Content.ReadAsStringAsync();
        saida.WriteLine($"paf/saude {(int)resposta.StatusCode}: {corpo}");
        return new Saude(resposta.StatusCode, JsonSerializer.Deserialize<SaudePafDto>(corpo, JsonSerializerOptions.Web)!);
    }

    private static async Task<T> EsperarAsync<T>(Func<Task<T>> ler, Func<T, bool> pronto, TimeSpan limite)
    {
        var relogio = Stopwatch.StartNew();
        while (true)
        {
            var valor = await ler();
            if (pronto(valor) || relogio.Elapsed > limite)
            {
                return valor;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500));
        }
    }

    // Synchronous on purpose: the Finbuckle context is an AsyncLocal that would not flow back from an async helper.
    private PafDbContext Contexto(IServiceScope scope, Guid? unidadeCorrente)
    {
        scope.ServiceProvider.GetRequiredService<IMultiTenantContextSetter>().MultiTenantContext = new MultiTenantContext<AppTenantInfo>(_tenant);
        if (unidadeCorrente is { } unidadeId)
        {
            scope.ServiceProvider.GetRequiredService<IUnidadeCorrente>().Definir(unidadeId);
        }

        return scope.ServiceProvider.GetRequiredService<PafDbContext>();
    }

    private async Task<Cenario> CenarioAsync(int limite)
    {
        using var scope = host.Services.CreateScope();
        var db = Contexto(scope, unidadeCorrente: null);
        var unidade = Unidade.Criar($"Loja {Guid.NewGuid():N}", documentoFiscal: null);
        var u = unidade.Id;
        var fila = Fila.Criar(u, "Caixa", "Caixa", setorId: null);
        var normal = Prioridade.Criar(u, "Normal", valor: 0);
        var guiche = Estacao.Criar(u, "Guichê 1", TipoEstacao.Guiche);
        var totem = Estacao.Criar(u, "Totem", TipoEstacao.Emissao);
        db.Unidades.Add(unidade);
        db.Filas.Add(fila);
        db.Prioridades.Add(normal);
        db.Estacoes.AddRange(guiche, totem);
        db.FilaPrioridades.Add(FilaPrioridade.Criar(u, fila.Id, normal.Id, "A", numeroInicial: 1, ordem: 0));
        db.EstacaoFilas.AddRange(
            EstacaoFila.Criar(u, guiche.Id, PapelEstacao.Guiche, fila.Id, 0),
            EstacaoFila.Criar(u, totem.Id, PapelEstacao.Emissao, fila.Id, 0));
        db.EstacaoPrioridades.Add(EstacaoPrioridade.Criar(u, guiche.Id, normal.Id, 0));
        // Started two days ago so the America/Sao_Paulo calendar day is inside the validity at any UTC hour.
        db.ContratosUnidade.Add(ContratoUnidade.Criar(u, DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-2)), null, "Teste", limite, PoliticaLimiteLicenca.Recusar));
        await db.SaveChangesAsync();
        return new Cenario(u, fila.Id, normal.Id, guiche.Id, totem.Id);
    }

    private async Task<string> CredencialTotemAsync(Cenario c)
    {
        VinculoEstacaoGeradoDto link;
        using (var scope = host.Services.CreateScope())
        {
            Contexto(scope, c.UnidadeId);
            link = await scope.ServiceProvider.GetRequiredService<IMediator>().Send(new GerarVinculoEstacaoCommand(c.Totem));
        }

        using var client = host.CreateClient();
        client.DefaultRequestHeaders.Add("tenant", _tenantId);
        using var resgate = await client.PostAsJsonAsync("/api/v1/paf/estacoes/vinculo/resgatar", new { token = link.Token });
        resgate.StatusCode.ShouldBe(HttpStatusCode.OK, await resgate.Content.ReadAsStringAsync());
        var credenciais = await resgate.Content.ReadFromJsonAsync<CredenciaisEstacaoDto>();
        return credenciais!.Credenciais.Single(x => x.Papel == PafPapeisEstacao.Emissao).AccessToken;
    }

    private async Task<HttpClient> AtendenteComGuicheAsync(Cenario c)
    {
        var usuario = await UsuarioAsync(c.UnidadeId);
        var cliente = Cliente((await TokenAsync(_tenantId, usuario.Email, usuario.Password)).AccessToken);
        using var selecao = await cliente.PostAsJsonAsync("/api/v1/paf/sessao/guiche", new { estacaoId = c.Guiche });
        selecao.StatusCode.ShouldBe(HttpStatusCode.NoContent, await selecao.Content.ReadAsStringAsync());
        return cliente;
    }

    private async Task EnvelhecerFichasAsync(Cenario c)
    {
        using var scope = host.Services.CreateScope();
        var db = Contexto(scope, unidadeCorrente: null);
        // Past the five-second guard of the call (seção 4) without sleeping in the test.
        await db.Database.ExecuteSqlAsync($"""UPDATE paf."Fichas" SET "CriadoEm" = "CriadoEm" - interval '6 seconds' WHERE "UnidadeId" = {c.UnidadeId} AND "TenantId" = {_tenantId}""");
    }

    private async Task<int> ContarFichasAsync(Cenario c)
    {
        using var scope = host.Services.CreateScope();
        return await Contexto(scope, c.UnidadeId).Fichas.CountAsync();
    }

    /// <summary>Sessões com linha fresca em AtividadeSessao na unidade: o que a reconstrução põe no conjunto.</summary>
    private async Task<List<Guid>> SessoesVivasAsync(Guid unidadeId)
    {
        using var scope = host.Services.CreateScope();
        var limite = DateTimeOffset.UtcNow.AddMinutes(-Licencas.PrazoMarcaMinutos);
        return await Contexto(scope, unidadeCorrente: null).AtividadesSessao.AsNoTracking()
            .Where(a => a.UnidadeId == unidadeId && a.UltimaAtividadeEm > limite)
            .Select(a => a.SessaoId)
            .ToListAsync();
    }

    private async Task<List<string>> MembrosAsync(Guid unidadeId)
    {
        var valkey = host.Services.GetRequiredService<IConnectionMultiplexer>();
        return (await valkey.GetDatabase().SortedSetRangeByRankAsync(ConjuntoLicencas.ChaveConjunto(_tenantId, unidadeId)))
            .Select(v => v.ToString())
            .ToList();
    }

    // ─── pessoas ─────────────────────────────────────────────────────

    private HttpClient Cliente(string accessToken)
    {
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        client.DefaultRequestHeaders.Add("tenant", _tenantId);
        return client;
    }

    private async Task<HttpClient> ClienteRootAsync()
    {
        var token = await TokenAsync(TestConstants.RootTenantId, TestConstants.RootAdminEmail, TestConstants.DefaultPassword);
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
        client.DefaultRequestHeaders.Add("tenant", TestConstants.RootTenantId);
        return client;
    }

    private async Task<HttpResponseMessage> LoginAsync(SeededUser usuario)
    {
        using var client = host.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{TestConstants.IdentityBasePath}/token/issue");
        request.Headers.Add("tenant", _tenantId);
        request.Content = JsonContent.Create(new { email = usuario.Email, password = usuario.Password });
        return await client.SendAsync(request);
    }

    private async Task<TokenResult> TokenAsync(string tenant, string email, string senha)
    {
        using var client = host.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{TestConstants.IdentityBasePath}/token/issue");
        request.Headers.Add("tenant", tenant);
        request.Content = JsonContent.Create(new { email, password = senha });
        using var resposta = await client.SendAsync(request);
        resposta.StatusCode.ShouldBe(HttpStatusCode.OK, await resposta.Content.ReadAsStringAsync());
        return (await resposta.Content.ReadFromJsonAsync<TokenResult>())!;
    }

    /// <summary>Usuário com login confirmado e escopo nas unidades dadas, a primeira como principal.</summary>
    private async Task<SeededUser> UsuarioAsync(params Guid[] unidades)
    {
        var id = Guid.NewGuid().ToString("N")[..8];
        var email = $"paf-dg-{id}@example.com";
        const string senha = "Test@1234!";
        using var registro = await _admin.PostAsJsonAsync($"{TestConstants.IdentityBasePath}/register", new
        {
            firstName = "Seeded",
            lastName = "User",
            email,
            userName = $"paf-dg-{id}",
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
        for (var ordem = 0; ordem < unidades.Length; ordem++)
        {
            db.UsuariosUnidade.Add(UsuarioUnidade.Criar(Guid.Parse(usuarioId), unidades[ordem], ordem));
        }

        await db.SaveChangesAsync();
        return new SeededUser(usuarioId, email, senha);
    }

    private async Task DarPapelAsync(string usuarioId, params string[] permissoes)
    {
        var nome = $"paf-papel-{Guid.NewGuid():N}"[..20];
        using var criar = await _admin.PostAsJsonAsync($"{TestConstants.IdentityBasePath}/roles", new { id = string.Empty, name = nome, description = "PAF teste" });
        criar.StatusCode.ShouldBe(HttpStatusCode.OK, await criar.Content.ReadAsStringAsync());
        using var json = JsonDocument.Parse(await criar.Content.ReadAsStringAsync());
        var papelId = json.RootElement.GetProperty("id").GetString()!;
        using var definir = await _admin.PutAsJsonAsync($"{TestConstants.IdentityBasePath}/{papelId}/permissions", new { roleId = papelId, permissions = permissoes });
        definir.StatusCode.ShouldBe(HttpStatusCode.OK, await definir.Content.ReadAsStringAsync());
        using var atribuir = await _admin.PostAsJsonAsync($"{TestConstants.IdentityBasePath}/users/{usuarioId}/roles", new
        {
            userId = usuarioId,
            userRoles = new[] { new { roleName = nome, enabled = true } },
        });
        atribuir.StatusCode.ShouldBe(HttpStatusCode.OK, await atribuir.Content.ReadAsStringAsync());
    }
}
