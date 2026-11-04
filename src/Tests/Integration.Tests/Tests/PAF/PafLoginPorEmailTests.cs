using System.IdentityModel.Tokens.Jwt;
using System.Text.Json;
using Finbuckle.MultiTenant;
using Finbuckle.MultiTenant.Abstractions;
using Finbuckle.MultiTenant.Stores;
using FSH.Framework.Mailing;
using Hangfire;
using FSH.Framework.Shared.Multitenancy;
using FSH.Modules.Identity.Data;
using FSH.Modules.Identity.Services;
using FSH.Modules.PAF.Sessao;
using Integration.Tests.Infrastructure;
using Integration.Tests.Tests.Sessions;

namespace Integration.Tests.Tests.PAF;

/// <summary>
/// O2 e O3: login e esqueci a senha sem empresa. O servidor acha a única empresa cliente ativa do e-mail e roda o
/// GenerateTokenCommand no escopo dela, então licença, sessão única e recusa de usuário sem unidade valem como no
/// login com empresa. Quem não tem a senha recebe sempre o mesmo 401.
/// </summary>
[Collection(FshCollectionDefinition.Name)]
public sealed class PafLoginPorEmailTests(FshWebApplicationFactory factory) : IAsyncLifetime
{
    private const string Rota = $"{TestConstants.IdentityBasePath}/token/issue-by-email";
    private const string RotaEsqueci = $"{TestConstants.IdentityBasePath}/forgot-password-by-email";
    private const string Perfil = $"{TestConstants.IdentityBasePath}/profile";
    private const string SenhaErrada = "Senha@Errada1";
    private const string AgenteDeTeste = "PafTeste/1.0";
    private readonly PafTenantCliente _tenant = new(factory, "paf-mail-login");

    public Task InitializeAsync() => _tenant.InicializarAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Login_EmailComCaixaEEspacos_EntraNaEmpresaDoEmailComSessaoETenantNoToken()
    {
        var unidade = await _tenant.CriarUnidadeAsync(limiteSessoes: 5);
        var usuario = await _tenant.CriarUsuarioAsync(unidade.UnidadeId);
        const string ip = "203.0.113.21";

        using var resposta = await LoginAsync($"  {usuario.Email.ToUpperInvariant()} ", usuario.Password, ip);

        resposta.StatusCode.ShouldBe(HttpStatusCode.OK, await resposta.Content.ReadAsStringAsync());
        var corpo = (await resposta.Content.ReadFromJsonAsync<RespostaLogin>())!;
        corpo.Tenant.ShouldBe(_tenant.TenantId);
        new JwtSecurityTokenHandler().ReadJwtToken(corpo.AccessToken).Claims
            .Single(c => c.Type == "tenant").Value.ShouldBe(_tenant.TenantId);

        var sessao = await SessaoAsync(PafTenantCliente.SessaoDoToken(corpo.AccessToken));
        sessao.IpAddress.ShouldBe(ip);
        sessao.UserAgent.ShouldBe(AgenteDeTeste);
        (await _tenant.AtividadesAsync(Guid.Parse(usuario.UserId))).Single().UnidadeId.ShouldBe(unidade.UnidadeId);

        using var cliente = _tenant.Cliente(corpo.AccessToken);
        (await cliente.GetAsync(Perfil)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var renovado = await _tenant.RenovarAsync(new TokenResult { AccessToken = corpo.AccessToken, RefreshToken = corpo.RefreshToken });
        renovado.StatusCode.ShouldBe(HttpStatusCode.OK, await renovado.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task EmailDesconhecido_SenhaErrada_EEmailParecido_MesmaRespostaSemContarFalha()
    {
        var unidade = await _tenant.CriarUnidadeAsync(limiteSessoes: 5);
        var usuario = await _tenant.CriarUsuarioAsync(unidade.UnidadeId);
        var susan = await IdentityUserSeeder.CreateLoginableUserAsync(factory, _tenant.Admin, "susan", _tenant.TenantId);

        using var desconhecido = await LoginAsync($"ninguem-{Guid.NewGuid():N}@paf.test", SenhaErrada, null);
        using var senhaErrada = await LoginAsync(usuario.Email, SenhaErrada, null);
        // U+017F (long s) upper-cases to S: same normalized e-mail, another mailbox.
        using var parecido = await LoginAsync("ſ" + susan.Email[1..], susan.Password, null);

        var referencia = await ProblemaAsync(desconhecido);
        desconhecido.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        senhaErrada.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        parecido.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await ProblemaAsync(senhaErrada)).ShouldBe(referencia);
        (await ProblemaAsync(parecido)).ShouldBe(referencia);
        (await FalhasDeAcessoAsync(susan.UserId)).ShouldBe(0, "o endereço parecido é outra pessoa: não conta falha na conta real");
        (await FalhasDeAcessoAsync(usuario.UserId)).ShouldBe(1, "a senha errada conta para o bloqueio do Identity");
    }

    [Fact]
    public async Task EmpresaVencidaOuDesativada_SoRevelaComASenhaCerta()
    {
        var outra = new PafTenantCliente(factory, "paf-mail-venc");
        await outra.InicializarAsync();
        var unidade = await outra.CriarUnidadeAsync(limiteSessoes: 5);
        var usuario = await outra.CriarUsuarioAsync(unidade.UnidadeId);
        using var desconhecido = await LoginAsync($"ninguem-{Guid.NewGuid():N}@paf.test", SenhaErrada, null);
        var referencia = await ProblemaAsync(desconhecido);

        await AlterarTenantAsync(outra.TenantId, t => t.ValidUpto = DateTime.UtcNow.AddDays(-30));
        using var vencidaSenhaErrada = await LoginAsync(usuario.Email, SenhaErrada, null);
        using var vencidaSenhaCerta = await LoginAsync(usuario.Email, usuario.Password, null);

        vencidaSenhaErrada.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await ProblemaAsync(vencidaSenhaErrada)).ShouldBe(referencia);
        vencidaSenhaCerta.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await ProblemaAsync(vencidaSenhaCerta)).ShouldNotBe(referencia, "com a senha certa o fluxo do kit diz que a empresa venceu");

        // Deactivated: out of the lookup altogether, so even the right password gets the unknown e-mail's answer.
        await AlterarTenantAsync(outra.TenantId, t =>
        {
            t.ValidUpto = DateTime.UtcNow.AddMonths(1);
            t.IsActive = false;
        });
        using var desativadaSenhaCerta = await LoginAsync(usuario.Email, usuario.Password, null);
        desativadaSenhaCerta.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await ProblemaAsync(desativadaSenhaCerta)).ShouldBe(referencia);
    }

    // token/issue checks the lockout before the password, so there a wrong password already says "locked" (423).
    [Fact]
    public async Task ContaBloqueada_SenhaErradaDaO401Generico_SoASenhaCertaVeOBloqueio()
    {
        var unidade = await _tenant.CriarUnidadeAsync(limiteSessoes: 5);
        var usuario = await _tenant.CriarUsuarioAsync(unidade.UnidadeId);
        await BloquearAsync(usuario.UserId);
        using var desconhecido = await LoginAsync($"ninguem-{Guid.NewGuid():N}@paf.test", SenhaErrada, null);

        using var errada = await LoginAsync(usuario.Email, SenhaErrada, null);
        using var certa = await LoginAsync(usuario.Email, usuario.Password, null);
        using var comEmpresa = await _tenant.LoginAsync(usuario.Email, SenhaErrada);

        errada.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await ProblemaAsync(errada)).ShouldBe(await ProblemaAsync(desconhecido));
        certa.StatusCode.ShouldBe(HttpStatusCode.Locked);
        comEmpresa.StatusCode.ShouldBe(HttpStatusCode.Locked, "o login com empresa continua como o kit");
    }

    // The PAF pipeline ran: licence and single session are GenerateTokenLicenseBehavior's, not the kit's.
    [Fact]
    public async Task SegundoLoginPorEmail_RevogaAPrimeiraSessao()
    {
        var unidade = await _tenant.CriarUnidadeAsync(limiteSessoes: 1);
        var usuario = await _tenant.CriarUsuarioAsync(unidade.UnidadeId);

        var primeiro = await EntrarAsync(usuario);
        var segundo = await EntrarAsync(usuario);

        using var antigo = _tenant.Cliente(primeiro.AccessToken);
        using var novo = _tenant.Cliente(segundo.AccessToken);
        (await antigo.GetAsync(Perfil)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await novo.GetAsync(Perfil)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var atividades = await _tenant.AtividadesAsync(Guid.Parse(usuario.UserId));
        atividades.Select(a => a.SessaoId).ShouldBe([PafTenantCliente.SessaoDoToken(segundo.AccessToken)]);
    }

    [Fact]
    public async Task UsuarioSemUnidade_SenhaCertaRecebeO403DoPaf_SenhaErradaO401Generico()
    {
        var semUnidade = await _tenant.CriarUsuarioAsync();

        using var errada = await LoginAsync(semUnidade.Email, SenhaErrada, null);
        using var certa = await LoginAsync(semUnidade.Email, semUnidade.Password, null);

        errada.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        certa.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await ProblemaAsync(certa)).Detail.ShouldBe(GenerateTokenLicenseBehavior.MensagemSemUnidade);
    }

    // O4: root is outside the lookup, so the operator's account is never found here, password or not.
    [Fact]
    public async Task ContaDaRaiz_RecusadaComARespostaDeEmailDesconhecido()
    {
        using var desconhecido = await LoginAsync($"ninguem-{Guid.NewGuid():N}@paf.test", SenhaErrada, null);
        using var raiz = await LoginAsync(TestConstants.RootAdminEmail, TestConstants.DefaultPassword, null);

        raiz.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await ProblemaAsync(raiz)).ShouldBe(await ProblemaAsync(desconhecido));
        (await new AuthHelper(factory).GetRootAdminTokenAsync()).AccessToken.ShouldNotBeNullOrWhiteSpace("token/issue com tenant=root continua");
    }

    [Fact]
    public async Task LimitePorEmail_SextaTentativaNoMinuto429_MesmoComASenhaCerta_OutroEmailSegue()
    {
        var unidade = await _tenant.CriarUnidadeAsync(limiteSessoes: 5);
        var usuario = await _tenant.CriarUsuarioAsync(unidade.UnidadeId);

        for (var i = 0; i < 4; i++)
        {
            using var errada = await LoginAsync(usuario.Email, SenhaErrada, null);
            errada.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }

        using var quinta = await LoginAsync(usuario.Email, usuario.Password, null);
        quinta.StatusCode.ShouldBe(HttpStatusCode.OK, await quinta.Content.ReadAsStringAsync());
        using var sexta = await LoginAsync(usuario.Email.ToUpperInvariant(), usuario.Password, null);
        sexta.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        (await ProblemaAsync(sexta)).Detail.ShouldBe(EmailLoginRateLimiter.Mensagem);

        using var outro = await LoginAsync($"outro-{Guid.NewGuid():N}@paf.test", SenhaErrada, null);
        outro.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task LimitePorIp_CemTentativasPassam_ACentesimaPrimeira429_OutroIpSegue()
    {
        const string ip = "203.0.113.40";
        var tentativas = Enumerable.Range(0, 100)
            .Select(async _ =>
            {
                using var r = await LoginAsync($"nat-{Guid.NewGuid():N}@paf.test", SenhaErrada, ip);
                return r.StatusCode;
            });
        (await Task.WhenAll(tentativas)).ShouldAllBe(s => s == HttpStatusCode.Unauthorized);

        using var excedente = await LoginAsync($"nat-{Guid.NewGuid():N}@paf.test", SenhaErrada, ip);
        using var outroIp = await LoginAsync($"nat-{Guid.NewGuid():N}@paf.test", SenhaErrada, "203.0.113.41");

        excedente.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        outroIp.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task EsqueciASenha_MesmaRespostaSempre_LinkLevaAEmpresa()
    {
        var usuario = await _tenant.CriarUsuarioAsync();

        using var anonimo = factory.CreateClient();
        using var conhecido = await anonimo.PostAsJsonAsync(RotaEsqueci, new { email = usuario.Email });
        using var desconhecido = await anonimo.PostAsJsonAsync(RotaEsqueci, new { email = $"ninguem-{Guid.NewGuid():N}@paf.test" });

        conhecido.StatusCode.ShouldBe(HttpStatusCode.OK, await conhecido.Content.ReadAsStringAsync());
        desconhecido.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await desconhecido.Content.ReadAsStringAsync()).ShouldBe(await conhecido.Content.ReadAsStringAsync());

        // The reset mail is a Hangfire job: its MailRequest argument, built inside the request, is what the API sent
        // (UserEmailLanguageTests.MailsTo explains why the capture itself is not reliable in this host).
        var redefinicoes = MensagensDoHangfire(usuario.Email)
            .Select(m => m.TextBody ?? m.Body ?? string.Empty)
            .Where(texto => texto.Contains("/reset-password?", StringComparison.Ordinal))
            .ToList();
        redefinicoes.Count.ShouldBe(1, "um link só, para a conta da empresa do e-mail");
        redefinicoes[0].ShouldContain($"tenant={_tenant.TenantId}");
    }

    private static List<MailRequest> MensagensDoHangfire(string para)
    {
        var monitor = JobStorage.Current.GetMonitoringApi();
        return monitor.SucceededJobs(0, 1000).Select(j => j.Value.Job)
            .Concat(monitor.ProcessingJobs(0, 1000).Select(j => j.Value.Job))
            .Concat(monitor.FailedJobs(0, 1000).Select(j => j.Value.Job))
            .Concat(monitor.Queues().SelectMany(q => monitor.EnqueuedJobs(q.Name, 0, 1000)).Select(j => j.Value.Job))
            .Where(j => j is not null)
            .SelectMany(j => j.Args.OfType<MailRequest>())
            .Where(m => m.To.Contains(para))
            .ToList();
    }

    private async Task<HttpResponseMessage> LoginAsync(string email, string senha, string? ip)
    {
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, Rota);
        request.Headers.UserAgent.ParseAdd(AgenteDeTeste);
        if (ip is not null)
        {
            request.Headers.Add(TestRemoteIpStartupFilter.RemoteIpHeader, ip);
        }

        request.Content = JsonContent.Create(new { email, password = senha });
        return await client.SendAsync(request);
    }

    private async Task<RespostaLogin> EntrarAsync(SeededUser usuario)
    {
        using var resposta = await LoginAsync(usuario.Email, usuario.Password, null);
        resposta.StatusCode.ShouldBe(HttpStatusCode.OK, await resposta.Content.ReadAsStringAsync());
        return (await resposta.Content.ReadFromJsonAsync<RespostaLogin>())!;
    }

    // The fields a caller can read; traceId and correlationId differ on every request by design.
    private static async Task<Problema> ProblemaAsync(HttpResponseMessage resposta)
    {
        using var json = JsonDocument.Parse(await resposta.Content.ReadAsStringAsync());
        var raiz = json.RootElement;
        string? Texto(string nome) => raiz.TryGetProperty(nome, out var valor) ? valor.ToString() : null;
        var chaves = string.Join(",", raiz.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        return new Problema((int)resposta.StatusCode, Texto("title"), Texto("detail"), Texto("type"), chaves);
    }

    private async Task<FSH.Modules.Identity.Domain.UserSession> SessaoAsync(Guid sessaoId)
    {
        using var scope = factory.Services.CreateScope();
        var tenant = await scope.ServiceProvider.GetRequiredService<IMultiTenantStore<AppTenantInfo>>().GetAsync(_tenant.TenantId);
        return await Identidade(scope, tenant!).UserSessions.AsNoTracking().SingleAsync(s => s.Id == sessaoId);
    }

    private async Task<int> FalhasDeAcessoAsync(string usuarioId)
    {
        using var scope = factory.Services.CreateScope();
        var tenant = await scope.ServiceProvider.GetRequiredService<IMultiTenantStore<AppTenantInfo>>().GetAsync(_tenant.TenantId);
        return await Identidade(scope, tenant!).Users.AsNoTracking().Where(u => u.Id == usuarioId).Select(u => u.AccessFailedCount).SingleAsync();
    }

    private async Task BloquearAsync(string usuarioId)
    {
        using var scope = factory.Services.CreateScope();
        var tenant = await scope.ServiceProvider.GetRequiredService<IMultiTenantStore<AppTenantInfo>>().GetAsync(_tenant.TenantId);
        var linhas = await Identidade(scope, tenant!).Users.Where(u => u.Id == usuarioId)
            .ExecuteUpdateAsync(u => u.SetProperty(x => x.LockoutEnd, DateTimeOffset.UtcNow.AddMinutes(15)));
        linhas.ShouldBe(1);
    }

    // Synchronous on purpose: the Finbuckle context is an AsyncLocal that would not flow back from an async helper.
    private static IdentityDbContext Identidade(IServiceScope scope, AppTenantInfo tenant)
    {
        scope.ServiceProvider.GetRequiredService<IMultiTenantContextSetter>().MultiTenantContext = new MultiTenantContext<AppTenantInfo>(tenant);
        return scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
    }

    // Straight to the EF store and into the distributed cache, as TenantExpiryEnforcementTests does: the cache
    // otherwise serves a 60-minute copy.
    private async Task AlterarTenantAsync(string tenantId, Action<AppTenantInfo> alterar)
    {
        using var scope = factory.Services.CreateScope();
        var stores = scope.ServiceProvider.GetServices<IMultiTenantStore<AppTenantInfo>>().ToList();
        var efStore = stores.First(s => s.GetType().Name.StartsWith("EFCoreStore", StringComparison.Ordinal));
        var tenant = await efStore.GetAsync(tenantId);
        tenant.ShouldNotBeNull();
        alterar(tenant);
        await efStore.UpdateAsync(tenant);
        var cacheStore = stores.FirstOrDefault(s => s.GetType() == typeof(DistributedCacheStore<AppTenantInfo>));
        if (cacheStore is not null)
        {
            await cacheStore.UpdateAsync(tenant);
        }
    }

    private sealed record RespostaLogin(string Tenant, string AccessToken, string RefreshToken);

    private sealed record Problema(int Status, string? Title, string? Detail, string? Type, string Chaves);
}
