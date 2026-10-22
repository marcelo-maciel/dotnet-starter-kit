using Finbuckle.MultiTenant.Abstractions;
using FSH.Framework.Shared.Multitenancy;
using FSH.Modules.Identity.Contracts.Services;
using FSH.Modules.Multitenancy.Contracts.Events;
using FSH.Modules.PAF.Contracts.Authorization;
using FSH.Modules.PAF.Contracts.v1.Filas;
using FSH.Modules.PAF.Data;
using FSH.Modules.PAF.Data.Configurations;
using FSH.Modules.PAF.Domain;
using FSH.Modules.PAF.Provisionamento;
using FSH.Modules.PAF.Sessao;
using Hangfire;
using Integration.Tests.Infrastructure;

namespace Integration.Tests.Tests.PAF;

/// <summary>
/// RF23 and D18 (C03): a tenant created through the kit gets its initial unidade from the tenant creation event, its
/// initial admin with escopo empresa and that unidade as principal through the Hangfire job, and the three PAF roles.
/// A client-tenant user with no unidade is refused at login.
/// </summary>
[Collection(FshCollectionDefinition.Name)]
public sealed class PafTenantCriadoTests(FshWebApplicationFactory factory) : IAsyncLifetime
{
    private readonly AuthHelper _auth = new(factory);
    private readonly PafTenantCliente _tenant = new(factory, "paf-c03");

    public Task InitializeAsync() => _tenant.InicializarAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task TenantNovoPelaApi_UnidadeInicialComContratoPadraoEAdminEmpresaOrdem0_LoginAceito()
    {
        var (tenantId, adminEmail) = await CriarTenantAsync("paf-c03a");

        // Before the event is delivered the PAF has not reached the tenant: the kit login stands (plano L23), which is
        // what the kit's own tests rely on when they log in right after provisioning.
        var antes = await LoginAsync(tenantId, adminEmail);
        antes.StatusCode.ShouldBe(HttpStatusCode.OK, await antes.Content.ReadAsStringAsync());

        await OutboxDrain.DrainAsync(factory.Services);
        var adminId = await AdminIdAsync(tenantId, adminEmail);
        var associacao = await EsperarAssociacaoAsync(tenantId, adminId);

        await using (var escopo = Escopo(await TenantAsync(tenantId)))
        {
            var db = escopo.ServiceProvider.GetRequiredService<PafDbContext>();
            var iniciais = await db.Unidades.Where(u => u.Inicial).ToListAsync();
            iniciais.Count.ShouldBe(1);
            iniciais[0].Nome.ShouldBe($"Tenant {tenantId}");
            iniciais[0].Ativa.ShouldBeTrue();
            associacao.UnidadeId.ShouldBe(iniciais[0].Id);
            associacao.Ordem.ShouldBe(0);
            (await db.UsuariosUnidade.CountAsync(u => u.UsuarioId == adminId)).ShouldBe(1);
            (await db.PerfisUsuario.SingleAsync(p => p.UsuarioId == adminId)).EscopoEmpresa.ShouldBeTrue();
            // RF23 (21/10/2026): the initial unidade is born with the default contract, valid from today in São Paulo.
            var contrato = await db.ContratosUnidade.IgnoreQueryFilters([PafQueryFilters.Unidade]).SingleAsync();
            contrato.UnidadeId.ShouldBe(iniciais[0].Id);
            contrato.Plano.ShouldBe("Inicial");
            contrato.LimiteSessoes.ShouldBe(5);
            contrato.Politica.ShouldBe(PoliticaLimiteLicenca.Recusar);
            contrato.InicioVigencia.ShouldBe(DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeBySystemTimeZoneId(DateTime.UtcNow, "America/Sao_Paulo")));
            contrato.FimVigencia.ShouldBeNull();
        }

        var comContrato = await LoginAsync(tenantId, adminEmail);
        comContrato.StatusCode.ShouldBe(HttpStatusCode.OK, await comContrato.Content.ReadAsStringAsync());

        await using (var escopo = Escopo(await TenantAsync(tenantId)))
        {
            await PapeisExatosAsync(escopo.ServiceProvider.GetRequiredService<IRoleService>());
        }
    }

    [Fact]
    public async Task EventoRepetidoEHandlersConcorrentes_UmaSoUnidadeInicialEUmaAssociacao()
    {
        var (tenantId, adminEmail) = await CriarTenantAsync("paf-c03b");
        var repetido = Evento(tenantId);

        // Eight deliveries at once (different event ids: the kit inbox only dedupes the same id), then the same id twice.
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => Handler().HandleAsync(Evento(tenantId), CancellationToken.None))));
        await Handler().HandleAsync(repetido, CancellationToken.None);
        await Handler().HandleAsync(repetido, CancellationToken.None);

        var adminId = await AdminIdAsync(tenantId, adminEmail);
        await EsperarAssociacaoAsync(tenantId, adminId);
        // Ten jobs were enqueued; give the in-memory Hangfire workers time to run the rest.
        await Task.Delay(TimeSpan.FromSeconds(3));

        await using var escopo = Escopo(await TenantAsync(tenantId));
        var db = escopo.ServiceProvider.GetRequiredService<PafDbContext>();
        (await db.Unidades.CountAsync(u => u.Inicial)).ShouldBe(1);
        (await db.ContratosUnidade.IgnoreQueryFilters([PafQueryFilters.Unidade]).CountAsync()).ShouldBe(1, "the contract is written with the unidade, so the losers write none");
        (await db.UsuariosUnidade.CountAsync(u => u.UsuarioId == adminId)).ShouldBe(1);
        (await db.PerfisUsuario.CountAsync(p => p.UsuarioId == adminId)).ShouldBe(1);

        // The database is what holds "one per tenant": a second initial unidade written directly is refused by name.
        db.Unidades.Add(Unidade.CriarInicial("Outra inicial"));
        var erro = await Should.ThrowAsync<DbUpdateException>(() => db.SaveChangesAsync());
        (erro.InnerException as Npgsql.PostgresException)?.ConstraintName.ShouldBe(UnidadeConfiguration.IndiceInicial);
    }

    [Fact]
    public async Task Job_AdminAindaInexistente_ReagendaAteOTetoEDepoisAssocia()
    {
        var (tenantId, adminEmail) = await CriarTenantAsync("paf-c03c");
        var adminId = await AdminIdAsync(tenantId, adminEmail);
        await TrocarAdminEmailAsync(tenantId, $"ainda-nao-existe-{Guid.NewGuid():N}@paf.test");

        await Job().ExecutarAsync(tenantId, 1, CancellationToken.None);
        (await AssociacaoAsync(tenantId, adminId)).ShouldBeNull();
        var agendados = AgendadosDoTenant(tenantId);
        agendados.Count.ShouldBe(1, "absent admin below the ceiling reschedules once");
        agendados[0].Tentativa.ShouldBe(2);
        foreach (var (id, _) in agendados)
        {
            factory.Services.GetRequiredService<IBackgroundJobClient>().Delete(id);
        }

        await Job().ExecutarAsync(tenantId, AssociacaoAdministradorInicial.Teto, CancellationToken.None);
        (await AssociacaoAsync(tenantId, adminId)).ShouldBeNull();
        AgendadosDoTenant(tenantId).ShouldBeEmpty("at the ceiling the job gives up instead of rescheduling");

        // No initial unidade yet: the PAF has not reached the tenant and the kit login stands.
        (await LoginAsync(tenantId, adminEmail)).StatusCode.ShouldBe(HttpStatusCode.OK);
        // The handler's part ran but the job gave up: a user with no unidade in a tenant the PAF reached is refused,
        // so the stuck admin is noticed instead of holding a licence-less token for good (L23).
        await using (var provisionado = Escopo(await TenantAsync(tenantId)))
        {
            await ActivatorUtilities.CreateInstance<ProvisionamentoTenantPaf>(provisionado.ServiceProvider)
                .GarantirUnidadeInicialAsync($"Tenant {tenantId}", CancellationToken.None);
        }

        var semUnidade = await LoginAsync(tenantId, adminEmail);
        semUnidade.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        PafSessaoLicencaTests.Detalhe(await semUnidade.Content.ReadAsStringAsync()).ShouldBe(GenerateTokenLicenseBehavior.MensagemSemUnidade);

        await TrocarAdminEmailAsync(tenantId, adminEmail);
        await Job().ExecutarAsync(tenantId, 3, CancellationToken.None);
        await Job().ExecutarAsync(tenantId, 4, CancellationToken.None);

        var associacao = await AssociacaoAsync(tenantId, adminId);
        associacao.ShouldNotBeNull();
        associacao.Ordem.ShouldBe(0);
        await using var escopo = Escopo(await TenantAsync(tenantId));
        var db = escopo.ServiceProvider.GetRequiredService<PafDbContext>();
        (await db.UsuariosUnidade.CountAsync(u => u.UsuarioId == adminId)).ShouldBe(1);
        (await db.Unidades.SingleAsync(u => u.Inicial)).Id.ShouldBe(associacao.UnidadeId);
    }

    [Fact]
    public async Task Job_AdminComEscopoJaConcedido_NaoMexe()
    {
        var unidade = await _tenant.CriarUnidadeAsync(limiteSessoes: 5);
        var usuario = await _tenant.CriarUsuarioAsync(unidade.UnidadeId);

        await using var escopo = Escopo(await TenantAsync(_tenant.TenantId));
        var provisionamento = ActivatorUtilities.CreateInstance<ProvisionamentoTenantPaf>(escopo.ServiceProvider);
        var gravou = await provisionamento.AssociarAdministradorAsync(_tenant.TenantId, Guid.Parse(usuario.UserId), _tenant.UnidadeInicial, CancellationToken.None);

        gravou.ShouldBeFalse();
        var db = escopo.ServiceProvider.GetRequiredService<PafDbContext>();
        var linhas = await db.UsuariosUnidade.Where(u => u.UsuarioId == Guid.Parse(usuario.UserId)).ToListAsync();
        linhas.ShouldHaveSingleItem().UnidadeId.ShouldBe(unidade.UnidadeId);
        (await db.PerfisUsuario.AnyAsync(p => p.UsuarioId == Guid.Parse(usuario.UserId))).ShouldBeFalse();
    }

    [Fact]
    public async Task PapeisPadrao_SemearDeNovo_NaoDuplicaEPreservaOQueOAdminMudou()
    {
        await using var escopo = Escopo(await TenantAsync(_tenant.TenantId));
        var papeis = escopo.ServiceProvider.GetRequiredService<IRoleService>();
        await PapeisPadrao.SemearAsync(papeis, CancellationToken.None);
        await PapeisExatosAsync(papeis);

        var gerente = await PapelAsync(papeis, PapeisPadrao.Gerente);
        await papeis.UpdatePermissionsAsync(gerente.Id, [PafPermissions.Filas.View], CancellationToken.None);
        await PapeisPadrao.SemearAsync(papeis, CancellationToken.None);

        foreach (var nome in new[] { PapeisPadrao.Administrador, PapeisPadrao.Gerente, PapeisPadrao.Atendente })
        {
            (await papeis.GetRolesAsync(1, 200, nome, CancellationToken.None)).Items.Count(r => r.Name == nome).ShouldBe(1);
        }

        (await papeis.GetWithPermissionsAsync(gerente.Id, CancellationToken.None)).Permissions!.ShouldBe([PafPermissions.Filas.View]);
        await papeis.UpdatePermissionsAsync(gerente.Id, [.. PapeisPadrao.PermissoesGerente], CancellationToken.None);
    }

    [Fact]
    public async Task Atendente_403EmCriarFila_200EmChamarProximo()
    {
        var operacao = new PafOperacao(factory, _tenant);
        var cenario = await operacao.CriarAsync();
        var usuario = await _tenant.CriarUsuarioAsync(cenario.UnidadeId);
        await SoComPapelAsync(usuario.UserId, PapeisPadrao.Atendente);
        using var cliente = _tenant.Cliente((await _tenant.EntrarAsync(usuario)).AccessToken);
        (await PermissoesAsync(cliente)).ShouldBe(PapeisPadrao.PermissoesAtendente, ignoreOrder: true);

        using var selecao = await cliente.PostAsJsonAsync("/api/v1/paf/sessao/guiche", new { estacaoId = cenario.GuicheA });
        selecao.StatusCode.ShouldBe(HttpStatusCode.NoContent, await selecao.Content.ReadAsStringAsync());
        using var fila = await cliente.PostAsJsonAsync("/api/v1/paf/filas", new CreateFilaCommand("Nova", "Nova", null, null, null, PafApi.SemAparencia, []));
        fila.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        using var totem = await operacao.TotemAsync(cenario);
        using var emissao = await PafOperacao.EmitirAsync(totem, cenario.Caixa, cenario.Normal, Guid.NewGuid().ToString("N"));
        emissao.StatusCode.ShouldBe(HttpStatusCode.OK, await emissao.Content.ReadAsStringAsync());
        await operacao.EnvelhecerFichasAsync(cenario.UnidadeId);
        using var chamada = await cliente.PostAsync(PafOperacao.RotaChamar, content: null);
        chamada.StatusCode.ShouldBe(HttpStatusCode.OK, await chamada.Content.ReadAsStringAsync());
    }

    // The liberar-guichê and empresa-parameter endpoints arrive in later tasks; until then the token's set is the proof.
    [Fact]
    public async Task Gerente_TemLiberarGuiche_NaoTemParametroDaEmpresaNemCadastro()
    {
        var unidade = await _tenant.CriarUnidadeAsync(limiteSessoes: 5);
        var usuario = await _tenant.CriarUsuarioAsync(unidade.UnidadeId);
        await SoComPapelAsync(usuario.UserId, PapeisPadrao.Gerente);
        using var cliente = _tenant.Cliente((await _tenant.EntrarAsync(usuario)).AccessToken);

        var permissoes = await PermissoesAsync(cliente);
        permissoes.ShouldBe(PapeisPadrao.PermissoesGerente, ignoreOrder: true);
        permissoes.ShouldContain(PafPermissions.Guiches.Liberar);
        permissoes.ShouldContain(PafPermissions.Estatisticas.ViewPorAtendente, "D10: o Gerente do cliente vê o tipo 4");
        permissoes.ShouldNotContain(PafPermissions.Parametros.UpdateEmpresa);
        using var fila = await PafApi.EnviarAsync(cliente, HttpMethod.Post, "/filas", null, new CreateFilaCommand("Nova", "Nova", null, null, null, PafApi.SemAparencia, []));
        fila.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        using var porAtendente = await cliente.GetAsync($"{PafApi.Base}/estatistica?tipo=4&dataInicial=2026-10-01&dataFinal=2026-10-18");
        porAtendente.StatusCode.ShouldBe(HttpStatusCode.OK, await porAtendente.Content.ReadAsStringAsync());
    }

    // ─── helpers ────────────────────────────────────────────────────────

    // Pinned here, not read from PapeisPadrao: the Atendente set is exactly the PAF basic permissions.
    private static readonly string[] AtendenteEsperado =
    [
        PafPermissions.Guiches.Selecionar, PafPermissions.Atendimentos.Operar, PafPermissions.Atendimentos.Encaminhar,
        PafPermissions.Atendimentos.SelecionarFicha, PafPermissions.Fichas.Emitir,
    ];

    private static async Task PapeisExatosAsync(IRoleService papeis)
    {
        var atendente = await PapelAsync(papeis, PapeisPadrao.Atendente);
        var gerente = await PapelAsync(papeis, PapeisPadrao.Gerente);
        var administrador = await PapelAsync(papeis, PapeisPadrao.Administrador);
        atendente.Permissions!.ShouldBe(AtendenteEsperado, ignoreOrder: true);
        gerente.Permissions!.ShouldBe(PapeisPadrao.PermissoesGerente, ignoreOrder: true);
        administrador.Permissions!.ShouldBe(PapeisPadrao.PermissoesAdministrador, ignoreOrder: true);

        // Administração without the company admin's exclusives (RF23) and without any kit permission (C03).
        administrador.Permissions!.ShouldNotContain(PafPermissions.Unidades.Create);
        administrador.Permissions!.ShouldNotContain(PafPermissions.Unidades.Update);
        administrador.Permissions!.ShouldNotContain(PafPermissions.Parametros.UpdateEmpresa);
        administrador.Permissions!.ShouldAllBe(p => p.StartsWith("Permissions.PAF.", StringComparison.Ordinal));
        var raiz = PafPermissions.All.Where(p => p.IsRoot).Select(p => p.Name).ToList();
        foreach (var papel in new[] { atendente, gerente, administrador })
        {
            papel.Permissions!.ShouldNotContain(p => raiz.Contains(p));
        }
    }

    private static async Task<FSH.Modules.Identity.Contracts.DTOs.RoleDto> PapelAsync(IRoleService papeis, string nome)
    {
        var papel = (await papeis.GetRolesAsync(1, 200, nome, CancellationToken.None)).Items.Single(r => r.Name == nome);
        return await papeis.GetWithPermissionsAsync(papel.Id, CancellationToken.None);
    }

    // Only the PAF role: the kit's Basic role (given at registration) would also grant the PAF basic permissions.
    private async Task SoComPapelAsync(string usuarioId, string papel)
    {
        using var atribuir = await _tenant.Admin.PostAsJsonAsync($"{TestConstants.IdentityBasePath}/users/{usuarioId}/roles", new
        {
            userId = usuarioId,
            userRoles = new[] { new { roleName = papel, enabled = true }, new { roleName = "Basic", enabled = false } },
        });
        atribuir.StatusCode.ShouldBe(HttpStatusCode.OK, await atribuir.Content.ReadAsStringAsync());
    }

    private static async Task<List<string>> PermissoesAsync(HttpClient cliente)
    {
        using var resposta = await cliente.GetAsync($"{TestConstants.IdentityBasePath}/permissions");
        resposta.StatusCode.ShouldBe(HttpStatusCode.OK, await resposta.Content.ReadAsStringAsync());
        return (await resposta.Content.ReadFromJsonAsync<List<string>>())!
            .Where(p => p.StartsWith("Permissions.PAF.", StringComparison.Ordinal)).ToList();
    }

    private TenantCriadoPafHandler Handler() =>
        ActivatorUtilities.CreateInstance<TenantCriadoPafHandler>(factory.Services);

    private AssociacaoAdministradorInicial Job() =>
        ActivatorUtilities.CreateInstance<AssociacaoAdministradorInicial>(factory.Services);

    private static TenantSubscribedIntegrationEvent Evento(string tenantId) =>
        new(Guid.NewGuid(), DateTime.UtcNow, tenantId, Guid.NewGuid().ToString("N"), "Teste", Guid.NewGuid(), "teste", DateTime.UtcNow, DateTime.UtcNow.AddMonths(1));

    private async Task<(string TenantId, string AdminEmail)> CriarTenantAsync(string prefixo)
    {
        var tenantId = $"{prefixo}-{Guid.NewGuid().ToString("N")[..8]}";
        var adminEmail = $"admin-{tenantId}@paf.test";
        using var root = await _auth.CreateRootAdminClientAsync();
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
        return (tenantId, adminEmail);
    }

    private async Task<HttpResponseMessage> LoginAsync(string tenantId, string email)
    {
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{TestConstants.IdentityBasePath}/token/issue");
        request.Headers.Add("tenant", tenantId);
        request.Content = JsonContent.Create(new { email, password = TestConstants.DefaultPassword });
        return await client.SendAsync(request);
    }

    private async Task<AppTenantInfo> TenantAsync(string tenantId) =>
        await ProvisionamentoTenantPaf.LerTenantAsync(factory.Services.GetRequiredService<IServiceScopeFactory>(), tenantId)
            ?? throw new InvalidOperationException($"Tenant '{tenantId}' não existe.");

    // Synchronous: the Finbuckle context is an AsyncLocal that would not flow back from an async helper.
    private AsyncServiceScope Escopo(AppTenantInfo tenant) =>
        ProvisionamentoTenantPaf.AbrirEscopo(factory.Services.GetRequiredService<IServiceScopeFactory>(), tenant);

    private async Task<Guid> AdminIdAsync(string tenantId, string adminEmail)
    {
        await using var escopo = Escopo(await TenantAsync(tenantId));
        var usuarios = await escopo.ServiceProvider.GetRequiredService<IUserService>().GetListAsync(CancellationToken.None);
        return Guid.Parse(usuarios.Single(u => u.Email == adminEmail).Id!);
    }

    private async Task<UsuarioUnidade?> AssociacaoAsync(string tenantId, Guid adminId)
    {
        await using var escopo = Escopo(await TenantAsync(tenantId));
        return await escopo.ServiceProvider.GetRequiredService<PafDbContext>().UsuariosUnidade.AsNoTracking()
            .SingleOrDefaultAsync(u => u.UsuarioId == adminId);
    }

    private async Task<UsuarioUnidade> EsperarAssociacaoAsync(string tenantId, Guid adminId)
    {
        var limite = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < limite)
        {
            if (await AssociacaoAsync(tenantId, adminId) is { } associacao)
            {
                return associacao;
            }

            await Task.Delay(250);
        }

        throw new TimeoutException($"Tenant '{tenantId}': o job não associou o administrador inicial em 30 s.");
    }

    private async Task TrocarAdminEmailAsync(string tenantId, string adminEmail)
    {
        using var scope = factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IMultiTenantStore<AppTenantInfo>>();
        var tenant = await store.GetAsync(tenantId) ?? throw new InvalidOperationException(tenantId);
        tenant.AdminEmail = adminEmail;
        (await store.UpdateAsync(tenant)).ShouldBeTrue();
    }

    private List<(string Id, int Tentativa)> AgendadosDoTenant(string tenantId) =>
        factory.Services.GetRequiredService<JobStorage>().GetMonitoringApi().ScheduledJobs(0, 1000)
            .Where(j => j.Value.Job?.Type == typeof(AssociacaoAdministradorInicial) && Equals(j.Value.Job.Args[0], tenantId))
            .Select(j => (j.Key, (int)j.Value.Job.Args[1]))
            .ToList();
}
