using Finbuckle.MultiTenant;
using Finbuckle.MultiTenant.Abstractions;
using FSH.Framework.Shared.Multitenancy;
using FSH.Modules.Identity.Contracts.Services;
using FSH.Modules.Identity.Contracts.v1.Impersonation;
using FSH.Modules.PAF.Domain;
using FSH.Modules.PAF.Sessao;
using Hangfire;
using Hangfire.Storage;
using Integration.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;
using Testcontainers.Redis;

namespace Integration.Tests.Tests.PAF;

/// <summary>
/// Job recorrente de S08 (ARQ seções 5 e 9): amostra de <c>ConsumosLicenca</c> por unidade com contrato vigente,
/// isolada por tenant e única por janela; marcas de <c>AtividadesSessao</c> vencidas apagadas; vínculos de guichê de
/// sessão sem marca viva e de concessão terminada soltos, com o atendimento em curso intacto. O job é chamado
/// direto, como o Hangfire faria; o do contêiner usa o conjunto do host, sem Valkey no harness.
/// </summary>
[Collection(FshCollectionDefinition.Name)]
public sealed class PafManutencaoLicencasTests(FshWebApplicationFactory factory) : IAsyncLifetime
{
    private static readonly TimeSpan Prazo = TimeSpan.FromMinutes(Licencas.PrazoMarcaMinutos);
    private readonly RedisContainer _valkey = new RedisBuilder("valkey/valkey:9.1.0-alpine").Build();
    private readonly PafTenantCliente _tenant = new(factory, "paf-ml");
    private readonly PafTenantCliente _outro = new(factory, "paf-ml2");
    private ConnectionMultiplexer _multiplexer = default!;

    public async Task InitializeAsync()
    {
        await Task.WhenAll(_valkey.StartAsync(), _tenant.InicializarAsync(), _outro.InicializarAsync());
        var opcoes = ConfigurationOptions.Parse(_valkey.GetConnectionString());
        opcoes.AbortOnConnectFail = false;
        _multiplexer = await ConnectionMultiplexer.ConnectAsync(opcoes);
    }

    public async Task DisposeAsync()
    {
        await _multiplexer.DisposeAsync();
        await _valkey.DisposeAsync();
    }

    private static ManutencaoLicencas JobDoHost(IServiceScope scope) => scope.ServiceProvider.GetRequiredService<ManutencaoLicencas>();

    private ManutencaoLicencas JobComValkey(ConjuntoLicencas conjunto) =>
        new(factory.Services.GetRequiredService<IServiceScopeFactory>(), conjunto, NullLogger<ManutencaoLicencas>.Instance);

    private ConjuntoLicencas ConjuntoValkey() => new([_multiplexer], Prazo, NullLogger<ConjuntoLicencas>.Instance);

    private async Task<AppTenantInfo> TenantAsync(string tenantId)
    {
        using var scope = factory.Services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<IMultiTenantStore<AppTenantInfo>>().GetAsync(tenantId)).ShouldNotBeNull();
    }

    /// <summary>Marca de sessão viva de uma sessão nova, com a idade dada; devolve a sessão.</summary>
    private static async Task<Guid> MarcaAsync(PafTenantCliente tenant, Guid unidadeId, int minutosAtras)
    {
        var sessaoId = Guid.NewGuid();
        await tenant.ExecutarAsync($"""
            INSERT INTO paf."AtividadesSessao" ("SessaoId", "UsuarioId", "UnidadeId", "UltimaAtividadeEm", "TenantId")
            VALUES ({sessaoId}, {Guid.NewGuid()}, {unidadeId}, now() - make_interval(mins => {minutosAtras}), {tenant.TenantId})
            """);
        return sessaoId;
    }

    private async Task RodarNoTenantAsync(AppTenantInfo tenant)
    {
        using var scope = factory.Services.CreateScope();
        await JobDoHost(scope).ExecutarNoTenantAsync(tenant, CancellationToken.None);
    }

    private async Task<List<ConsumoLicenca>> AmostrasAsync(PafTenantCliente tenant, Guid unidadeId)
    {
        using var scope = factory.Services.CreateScope();
        return await tenant.Contexto(scope, unidadeCorrente: unidadeId).ConsumosLicenca.AsNoTracking()
            .Where(c => c.UnidadeId == unidadeId && c.Tipo == TipoConsumoLicenca.Amostra).ToListAsync();
    }

    private async Task<int> LinhasDeOutroTenantAsync(PafTenantCliente tenant, Guid unidadeId)
    {
        using var scope = factory.Services.CreateScope();
        var db = tenant.Contexto(scope, unidadeCorrente: null);
        return await db.Database.SqlQuery<int>($"""
            SELECT count(*)::int AS "Value" FROM paf."ConsumosLicenca"
             WHERE "UnidadeId" = {unidadeId} AND "TenantId" <> {tenant.TenantId}
            """).SingleAsync();
    }

    private async Task<DateTimeOffset> JanelaAtualAsync()
    {
        using var scope = factory.Services.CreateScope();
        var db = _tenant.Contexto(scope, unidadeCorrente: null);
        return await db.Database.SqlQuery<DateTimeOffset>($"""
            SELECT date_bin(make_interval(mins => {ManutencaoLicencas.JanelaAmostraMinutos}), now(), timestamptz '2000-01-01 00:00:00+00') AS "Value"
            """).SingleAsync();
    }

    [Fact]
    public void Job_RegistradoNoHangfireComOCronDaManutencao()
    {
        using var conexao = factory.Services.GetRequiredService<JobStorage>().GetConnection();

        var job = conexao.GetRecurringJobs().Where(j => j.Id == ManutencaoLicencas.IdRecorrente).ShouldHaveSingleItem();

        job.Cron.ShouldBe(ManutencaoLicencas.Cron);
        job.Job.Type.ShouldBe(typeof(ManutencaoLicencas));
        job.Job.Method.Name.ShouldBe(nameof(ManutencaoLicencas.ExecutarAsync));
    }

    [Fact]
    public async Task DoisTenantsDuasUnidades_AmostraPorUnidadeComContrato_Isolada()
    {
        var u1 = await _tenant.CriarUnidadeAsync(limiteSessoes: 3);
        var u2 = await _tenant.CriarUnidadeAsync(limiteSessoes: 5);
        var semContrato = await _tenant.CriarUnidadeAsync(limiteSessoes: null);
        var v1 = await _outro.CriarUnidadeAsync(limiteSessoes: 2);
        await MarcaAsync(_tenant, u1.UnidadeId, 0);
        await MarcaAsync(_tenant, u1.UnidadeId, 9);
        await MarcaAsync(_tenant, u2.UnidadeId, 0);
        await MarcaAsync(_tenant, u2.UnidadeId, 11);
        await MarcaAsync(_tenant, semContrato.UnidadeId, 0);
        await MarcaAsync(_outro, v1.UnidadeId, 0);

        using (var scope = factory.Services.CreateScope())
        {
            await JobDoHost(scope).ExecutarAsync(CancellationToken.None);
        }

        var a1 = (await AmostrasAsync(_tenant, u1.UnidadeId)).ShouldHaveSingleItem();
        (a1.Ocupadas, a1.Limite, a1.SessaoId).ShouldBe((2, 3, (Guid?)null));
        var a2 = (await AmostrasAsync(_tenant, u2.UnidadeId)).ShouldHaveSingleItem();
        (a2.Ocupadas, a2.Limite).ShouldBe((1, 5), "a marca de 11 minutos não conta");
        (await AmostrasAsync(_tenant, semContrato.UnidadeId)).ShouldBeEmpty("unidade sem contrato não tem limite a conferir");
        var b1 = (await AmostrasAsync(_outro, v1.UnidadeId)).ShouldHaveSingleItem();
        (b1.Ocupadas, b1.Limite).ShouldBe((1, 2));
        (await LinhasDeOutroTenantAsync(_tenant, u1.UnidadeId)).ShouldBe(0);
        (await LinhasDeOutroTenantAsync(_outro, v1.UnidadeId)).ShouldBe(0);
    }

    [Fact]
    public async Task ExecucoesNaMesmaJanela_ConcorrentesESeguidas_NaoDuplicam()
    {
        var unidade = await _tenant.CriarUnidadeAsync(limiteSessoes: 4);
        await MarcaAsync(_tenant, unidade.UnidadeId, 0);
        var tenant = await TenantAsync(_tenant.TenantId);
        var janelaAntes = await JanelaAtualAsync();

        using (var a = factory.Services.CreateScope())
        using (var b = factory.Services.CreateScope())
        {
            await Task.WhenAll(
                JobDoHost(a).ExecutarNoTenantAsync(tenant, CancellationToken.None),
                JobDoHost(b).ExecutarNoTenantAsync(tenant, CancellationToken.None));
        }

        await RodarNoTenantAsync(tenant);

        var janelaDepois = await JanelaAtualAsync();
        var amostras = await AmostrasAsync(_tenant, unidade.UnidadeId);
        // Three runs land in one window, or in two when the test straddles a boundary: never two rows in one window.
        amostras.GroupBy(x => x.RegistradoEm).ShouldAllBe(g => g.Count() == 1);
        amostras.Count.ShouldBe(janelaAntes == janelaDepois ? 1 : 2);
        amostras.ShouldAllBe(x => x.RegistradoEm == janelaAntes || x.RegistradoEm == janelaDepois);
    }

    [Fact]
    public async Task ComValkey_ContaOConjuntoSemReservas_ENaoReconstruido_ContaAsMarcas()
    {
        var conjunto = ConjuntoValkey();
        (await conjunto.MarcarReconstruidoAsync(CancellationToken.None)).ShouldBeTrue();
        var tenant = await TenantAsync(_tenant.TenantId);
        var pelaContagemDoConjunto = await _tenant.CriarUnidadeAsync(limiteSessoes: 5);
        foreach (var membro in new[] { Guid.NewGuid().ToString(), Guid.NewGuid().ToString(), ConjuntoLicencas.NovaReserva() })
        {
            (await conjunto.RestaurarAsync(_tenant.TenantId, membro, pelaContagemDoConjunto.UnidadeId, Prazo, CancellationToken.None)).ShouldBeTrue();
        }

        await JobComValkey(conjunto).ExecutarNoTenantAsync(tenant, CancellationToken.None);

        (await AmostrasAsync(_tenant, pelaContagemDoConjunto.UnidadeId)).ShouldHaveSingleItem().Ocupadas
            .ShouldBe(2, "dois membros de sessão; a reserva e as marcas do PostgreSQL (nenhuma) não contam");

        // Valkey reachable but not rebuilt: the set is not the count that holds, the live marks are.
        var pelasMarcas = await _tenant.CriarUnidadeAsync(limiteSessoes: 5);
        (await conjunto.RestaurarAsync(_tenant.TenantId, Guid.NewGuid().ToString(), pelasMarcas.UnidadeId, Prazo, CancellationToken.None)).ShouldBeTrue();
        await MarcaAsync(_tenant, pelasMarcas.UnidadeId, 0);
        await MarcaAsync(_tenant, pelasMarcas.UnidadeId, 0);
        await MarcaAsync(_tenant, pelasMarcas.UnidadeId, 0);
        await _multiplexer.GetDatabase().KeyDeleteAsync("paf:licencas:reconstruido");
        await _tenant.ExecutarAsync($"""
            DELETE FROM paf."ConsumosLicenca" WHERE "UnidadeId" = {pelasMarcas.UnidadeId} AND "TenantId" = {_tenant.TenantId}
            """);

        await JobComValkey(conjunto).ExecutarNoTenantAsync(tenant, CancellationToken.None);

        (await AmostrasAsync(_tenant, pelasMarcas.UnidadeId)).ShouldHaveSingleItem().Ocupadas.ShouldBe(3);
    }

    [Fact]
    public async Task MarcaDe11MinutosApagada_De9MinutosMantida()
    {
        var unidade = await _tenant.CriarUnidadeAsync(limiteSessoes: 5);
        var vencida = await MarcaAsync(_tenant, unidade.UnidadeId, 11);
        var viva = await MarcaAsync(_tenant, unidade.UnidadeId, 9);

        await RodarNoTenantAsync(await TenantAsync(_tenant.TenantId));

        using var scope = factory.Services.CreateScope();
        var restantes = await _tenant.Contexto(scope, unidadeCorrente: null).AtividadesSessao.AsNoTracking()
            .Where(a => a.SessaoId == vencida || a.SessaoId == viva).Select(a => a.SessaoId).ToListAsync();
        restantes.ShouldBe([viva]);
    }

    [Fact]
    public async Task VinculosOrfaosSoltos_VivosMantidos_AtendimentoEmCursoIntacto()
    {
        var x = await _tenant.CriarUnidadeAsync(limiteSessoes: 10);
        var y = await _tenant.CriarUnidadeAsync(limiteSessoes: 10);
        var z = await _tenant.CriarUnidadeAsync(limiteSessoes: 10);
        var sessaoViva = await MarcaAsync(_tenant, x.UnidadeId, 0);
        var sessaoSemMarca = Guid.NewGuid();
        var sessaoVencida = await MarcaAsync(_tenant, y.UnidadeId, 11);
        await _tenant.ChamarNoGuicheAsync(y);
        var concessaoViva = await CriarConcessaoAsync();

        var vivo = SessaoEstacao.CriarParaSessao(x.GuicheA, sessaoViva);
        var semMarca = SessaoEstacao.CriarParaSessao(x.GuicheB, sessaoSemMarca);
        var vencido = SessaoEstacao.CriarParaSessao(y.GuicheA, sessaoVencida);
        var concessaoVencida = SessaoEstacao.CriarParaConcessao(y.GuicheB, Guid.NewGuid(), DateTimeOffset.UtcNow.AddMinutes(-1));
        var concessaoDesconhecida = SessaoEstacao.CriarParaConcessao(z.GuicheA, Guid.NewGuid(), DateTimeOffset.UtcNow.AddMinutes(30));
        var concessaoAtiva = SessaoEstacao.CriarParaConcessao(z.GuicheB, Guid.ParseExact(concessaoViva.Jti, "N"), DateTimeOffset.UtcNow.AddMinutes(30));
        Atendimento emCursoAntes;
        using (var scope = factory.Services.CreateScope())
        {
            var db = _tenant.Contexto(scope, unidadeCorrente: y.UnidadeId);
            db.SessoesEstacao.AddRange(vivo, semMarca, vencido, concessaoVencida, concessaoDesconhecida, concessaoAtiva);
            await db.SaveChangesAsync();
            emCursoAntes = await db.Atendimentos.AsNoTracking().SingleAsync(a => a.EstacaoId == y.GuicheA);
        }

        emCursoAntes.Estado.ShouldBe(EstadoAtendimento.Chamado);
        var tenant = await TenantAsync(_tenant.TenantId);
        await RodarNoTenantAsync(tenant);
        var primeira = await VinculosAsync();

        primeira[vivo.Id].ShouldBeNull("sessão com marca viva segue no guichê");
        primeira[concessaoAtiva.Id].ShouldBeNull("concessão ativa segue no guichê");
        primeira[semMarca.Id].ShouldNotBeNull();
        primeira[vencido.Id].ShouldNotBeNull();
        primeira[concessaoVencida.Id].ShouldNotBeNull();
        primeira[concessaoDesconhecida.Id].ShouldNotBeNull("concessão desconhecida conta como encerrada, como no kit");

        using (var scope = factory.Services.CreateScope())
        {
            var depois = await _tenant.Contexto(scope, unidadeCorrente: y.UnidadeId).Atendimentos.AsNoTracking()
                .SingleAsync(a => a.Id == emCursoAntes.Id);
            (depois.Estado, depois.Versao, depois.EstacaoId).ShouldBe((emCursoAntes.Estado, emCursoAntes.Versao, emCursoAntes.EstacaoId));
        }

        // A second run changes nothing: the releases are conditional on the link still being active.
        await RodarNoTenantAsync(tenant);
        (await VinculosAsync()).ShouldBe(primeira, ignoreOrder: true);

        async Task<Dictionary<Guid, DateTimeOffset?>> VinculosAsync()
        {
            Guid[] ids = [vivo.Id, semMarca.Id, vencido.Id, concessaoVencida.Id, concessaoDesconhecida.Id, concessaoAtiva.Id];
            using var scope = factory.Services.CreateScope();
            return await _tenant.Contexto(scope, unidadeCorrente: null).SessoesEstacao.AsNoTracking()
                .Where(v => ids.Contains(v.Id)).ToDictionaryAsync(v => v.Id, v => v.LiberadoEm);
        }
    }

    private async Task<ImpersonationGrantDto> CriarConcessaoAsync()
    {
        using var scope = factory.Services.CreateScope();
        var root = await scope.ServiceProvider.GetRequiredService<IMultiTenantStore<AppTenantInfo>>().GetAsync(TestConstants.RootTenantId);
        scope.ServiceProvider.GetRequiredService<IMultiTenantContextSetter>().MultiTenantContext =
            new MultiTenantContext<AppTenantInfo>(root.ShouldNotBeNull());
        var agora = DateTime.UtcNow;
        return await scope.ServiceProvider.GetRequiredService<IImpersonationGrantService>().CreateAsync(new CreateGrantInput(
            Jti: Guid.NewGuid().ToString("N"),
            ActorUserId: Guid.NewGuid().ToString(),
            ActorUserName: "suporte",
            ActorTenantId: TestConstants.RootTenantId,
            ImpersonatedUserId: Guid.NewGuid().ToString(),
            ImpersonatedUserName: "atendente",
            ImpersonatedTenantId: _tenant.TenantId,
            Reason: "S08",
            StartedAtUtc: agora,
            ExpiresAtUtc: agora.AddMinutes(30),
            ClientId: null,
            IpAddress: null,
            UserAgent: null), CancellationToken.None);
    }
}
