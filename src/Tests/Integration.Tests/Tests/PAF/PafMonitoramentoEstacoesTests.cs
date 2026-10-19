using System.Diagnostics;
using System.Security.Claims;
using Finbuckle.MultiTenant.Abstractions;
using FSH.Framework.Shared.Multitenancy;
using FSH.Modules.PAF.Contracts.Authorization;
using FSH.Modules.PAF.Contracts.v1.Estacoes;
using FSH.Modules.PAF.Data;
using FSH.Modules.PAF.Domain;
using FSH.Modules.PAF.Features.v1.Estacoes.ListEstacoesOperador;
using FSH.Modules.PAF.Sessao;
using FSH.Modules.PAF.TempoReal;
using Integration.Tests.Infrastructure;
using Mediator;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using Testcontainers.Redis;

namespace Integration.Tests.Tests.PAF;

/// <summary>
/// Monitoramento de estação (RNF20, O13): o hub grava o instante do PostgreSQL da última conexão e da última queda na
/// coluna do papel da credencial; a tela de suporte do operador Codefi lê um cliente por vez, com o estado presente pelo
/// registro de conexões e o aviso em horário de operação (D24). O host do harness sobe sem Valkey, então os casos de
/// outra instância e de instância morta montam o registro sobre um Valkey real, com relógio simulado.
/// </summary>
[Collection(FshCollectionDefinition.Name)]
public sealed class PafMonitoramentoEstacoesTests(FshWebApplicationFactory factory) : IAsyncLifetime
{
    private const string Rota = "/operador/estacoes";
    private static readonly TimeSpan Espera = TimeSpan.FromSeconds(5);

    private readonly RedisContainer _valkey = new RedisBuilder("valkey/valkey:9.1.0-alpine").Build();
    private readonly PafTenantCliente _tenantA = new(factory, "paf-o13a");
    private readonly PafTenantCliente _tenantB = new(factory, "paf-o13b");
    private readonly List<HubConnection> _hubs = [];
    private ConnectionMultiplexer _multiplexer = default!;

    public async Task InitializeAsync()
    {
        await Task.WhenAll(_valkey.StartAsync(), _tenantA.InicializarAsync(), _tenantB.InicializarAsync());
        var opcoes = ConfigurationOptions.Parse(_valkey.GetConnectionString());
        opcoes.AbortOnConnectFail = false;
        _multiplexer = await ConnectionMultiplexer.ConnectAsync(opcoes);
    }

    public async Task DisposeAsync()
    {
        foreach (var hub in _hubs)
        {
            await hub.DisposeAsync();
        }

        await _multiplexer.DisposeAsync();
        await _valkey.DisposeAsync();
    }

    [Fact]
    public async Task ConexaoEQuedaDoTotem_GravamSoAColunaDoPapel_ComOInstanteDoBanco()
    {
        var loja = await CriarLojaAsync(_tenantA, Janela.SemRestricao);
        var credencial = (await CredenciaisAsync(_tenantA, loja.UnidadeId, loja.Totem))[PafPapeisEstacao.Emissao];

        var antes = await AgoraDoBancoAsync(_tenantA);
        var hub = await ConectarAsync(_tenantA, credencial);
        var depois = await AgoraDoBancoAsync(_tenantA);

        var conectado = await EstacaoAsync(_tenantA, loja.UnidadeId, loja.Totem);
        conectado.UltimaConexaoEmissaoEm.ShouldNotBeNull().ShouldBeInRange(antes, depois, "o instante é o now() do PostgreSQL");
        conectado.UltimaQuedaEmissaoEm.ShouldBeNull();
        conectado.UltimaConexaoPainelEm.ShouldBeNull("só a coluna do papel da credencial muda");
        conectado.UltimaQuedaPainelEm.ShouldBeNull();

        await DesconectarAsync(hub);
        var caido = await EsperarAsync(_tenantA, loja.UnidadeId, loja.Totem, e => e.UltimaQuedaEmissaoEm is not null);

        caido.UltimaQuedaEmissaoEm!.Value.ShouldBeGreaterThan(conectado.UltimaConexaoEmissaoEm.Value);
        caido.UltimaConexaoEmissaoEm.ShouldBe(conectado.UltimaConexaoEmissaoEm);
        caido.UltimaConexaoPainelEm.ShouldBeNull();
        caido.UltimaQuedaPainelEm.ShouldBeNull();
    }

    [Fact]
    public async Task EstacaoCombinada_QuedaDoPainelNaoTocaAEmissao_ETelaMostraDuasLinhas()
    {
        var loja = await CriarLojaAsync(_tenantA, Janela.SemRestricao);
        var credenciais = await CredenciaisAsync(_tenantA, loja.UnidadeId, loja.Combinada);
        using var raiz = await RaizNoTenantAsync(_tenantA);

        await ConectarAsync(_tenantA, credenciais[PafPapeisEstacao.Emissao]);
        var painel = await ConectarAsync(_tenantA, credenciais[PafPapeisEstacao.Painel]);
        var ambos = await EstacaoAsync(_tenantA, loja.UnidadeId, loja.Combinada);
        await DesconectarAsync(painel);
        var depois = await EsperarAsync(_tenantA, loja.UnidadeId, loja.Combinada, e => e.UltimaQuedaPainelEm is not null);

        depois.UltimaQuedaEmissaoEm.ShouldBeNull("a queda do painel não toca a emissão");
        depois.UltimaConexaoEmissaoEm.ShouldBe(ambos.UltimaConexaoEmissaoEm);
        depois.UltimaConexaoPainelEm.ShouldBe(ambos.UltimaConexaoPainelEm);

        var linhas = (await ListarAsync(raiz)).Papeis.Where(p => p.EstacaoId == loja.Combinada).ToList();
        linhas.Select(p => p.Papel).ShouldBe([PafPapeisEstacao.Emissao, PafPapeisEstacao.Painel]);
        linhas[0].Conectada.ShouldBeTrue();
        linhas[0].Aviso.ShouldBeFalse();
        linhas[1].Conectada.ShouldBeFalse();
        linhas[1].Aviso.ShouldBeTrue("estação ativa, papel sem conexão, fila sem janela = em operação");
        linhas[1].UltimaQuedaEm.ShouldBe(depois.UltimaQuedaPainelEm);
    }

    [Fact]
    public async Task ReconexaoRapida_QuedaDaAntigaDepoisDaNova_NaoApareceComoCaida()
    {
        var loja = await CriarLojaAsync(_tenantA, Janela.SemRestricao);
        var credencial = (await CredenciaisAsync(_tenantA, loja.UnidadeId, loja.Painel))[PafPapeisEstacao.Painel];
        using var raiz = await RaizNoTenantAsync(_tenantA);

        var antiga = await ConectarAsync(_tenantA, credencial);
        var nova = await ConectarAsync(_tenantA, credencial);
        var reconectado = await EstacaoAsync(_tenantA, loja.UnidadeId, loja.Painel);
        await DesconectarAsync(antiga);
        await Task.Delay(TimeSpan.FromSeconds(1));

        var depoisDaAntiga = await EstacaoAsync(_tenantA, loja.UnidadeId, loja.Painel);
        depoisDaAntiga.UltimaQuedaPainelEm.ShouldBeNull("a nova conexão do papel continua viva");
        depoisDaAntiga.UltimaConexaoPainelEm.ShouldBe(reconectado.UltimaConexaoPainelEm);
        var linha = (await ListarAsync(raiz)).Papeis.Single(p => p.EstacaoId == loja.Painel);
        linha.Conectada.ShouldBeTrue();
        linha.Aviso.ShouldBeFalse();

        await DesconectarAsync(nova);
        await EsperarAsync(_tenantA, loja.UnidadeId, loja.Painel, e => e.UltimaQuedaPainelEm is not null);
        (await ListarAsync(raiz)).Papeis.Single(p => p.EstacaoId == loja.Painel).Conectada
            .ShouldBeFalse("a última conexão do papel caiu");
    }

    [Fact]
    public async Task ReconexaoEmOutraInstancia_QuedaDaAntigaSoEGravadaQuandoANovaCai()
    {
        var loja = await CriarLojaAsync(_tenantA, Janela.SemRestricao);
        var relogio = new RelogioDeTeste();
        var primeira = NovaInstancia(relogio);
        var segunda = NovaInstancia(relogio);
        var conexao = new ConexaoEstacao(_tenantA.TenantId, PafPapeisEstacao.Emissao, loja.Totem, loja.UnidadeId, Guid.NewGuid());
        var antiga = new ContextoDeTeste("antiga-" + Guid.NewGuid().ToString("N"));
        var nova = new ContextoDeTeste("nova-" + Guid.NewGuid().ToString("N"));

        await primeira.Conexoes.RegistrarAsync(antiga, conexao, CancellationToken.None);
        await primeira.Monitoramento.RegistrarConexaoAsync(conexao, CancellationToken.None);
        await segunda.Conexoes.RegistrarAsync(nova, conexao, CancellationToken.None);
        await segunda.Monitoramento.RegistrarConexaoAsync(conexao, CancellationToken.None);
        await primeira.Conexoes.RemoverAsync(antiga.ConnectionId, CancellationToken.None);
        await primeira.Monitoramento.RegistrarQuedaAsync(conexao, CancellationToken.None);

        (await EstacaoAsync(_tenantA, loja.UnidadeId, loja.Totem)).UltimaQuedaEmissaoEm
            .ShouldBeNull("a primeira instância não segura a nova conexão, mas o registro a mostra viva");

        await segunda.Conexoes.RemoverAsync(nova.ConnectionId, CancellationToken.None);
        await segunda.Monitoramento.RegistrarQuedaAsync(conexao, CancellationToken.None);
        (await EstacaoAsync(_tenantA, loja.UnidadeId, loja.Totem)).UltimaQuedaEmissaoEm.ShouldNotBeNull();
    }

    [Fact]
    public async Task InstanciaMorta_PapelViraCaidoQuandoAEntradaDoRegistroVence()
    {
        var loja = await CriarLojaAsync(_tenantA, Janela.SemRestricao);
        var relogio = new RelogioDeTeste();
        var morta = NovaInstancia(relogio);
        var conexao = new ConexaoEstacao(_tenantA.TenantId, PafPapeisEstacao.Painel, loja.Painel, loja.UnidadeId, Guid.NewGuid());

        // The instance registers and writes the connection, then dies: no disconnect, no renewal.
        await morta.Conexoes.RegistrarAsync(new ContextoDeTeste("morta-" + Guid.NewGuid().ToString("N")), conexao, CancellationToken.None);
        await morta.Monitoramento.RegistrarConexaoAsync(conexao, CancellationToken.None);

        (await PapelPeloHandlerAsync(morta.Registro, loja.Painel)).Conectada.ShouldBeTrue();

        relogio.Avancar(Validade - TimeSpan.FromSeconds(1));
        (await PapelPeloHandlerAsync(morta.Registro, loja.Painel)).Conectada.ShouldBeTrue("a entrada ainda não venceu");

        relogio.Avancar(TimeSpan.FromSeconds(2));
        var vencida = await PapelPeloHandlerAsync(morta.Registro, loja.Painel);
        vencida.Conectada.ShouldBeFalse("vencida a entrada, nenhuma instância segura o painel");
        vencida.Aviso.ShouldBeTrue();
        vencida.UltimaQuedaEm.ShouldBeNull("as colunas ainda dizem conectado: o registro decide o estado presente");

        // Same database rows without the registry (Valkey absent): the columns decide, and a dead instance stays hidden.
        var semValkey = new RegistroConexoes([], Options.Create(new OpcoesSessaoPaf()), relogio, NullLogger<RegistroConexoes>.Instance);
        var (papel, peloRegistro) = await PapelEFonteAsync(semValkey, loja.Painel);
        peloRegistro.ShouldBeFalse();
        papel.Conectada.ShouldBeTrue();
    }

    [Fact]
    public async Task RaizNoTenantA_LeSoAsEstacoesDeA_SemGuicheNemAdministracao()
    {
        var lojaA = await CriarLojaAsync(_tenantA, Janela.SemRestricao);
        var lojaB = await CriarLojaAsync(_tenantB, Janela.SemRestricao);
        using var raizEmA = await RaizNoTenantAsync(_tenantA);

        var papeis = (await ListarAsync(raizEmA)).Papeis;

        papeis.ShouldContain(p => p.EstacaoId == lojaA.Totem);
        papeis.ShouldNotContain(p => p.UnidadeId == lojaB.UnidadeId || p.EstacaoId == lojaB.Totem || p.EstacaoId == lojaB.Combinada);
        papeis.ShouldNotContain(p => p.EstacaoId == lojaA.Guiche, "o guichê conecta pela sessão, fora do monitoramento");
        papeis.Where(p => p.UnidadeId == lojaA.UnidadeId).Select(p => p.EstacaoId).Distinct()
            .ShouldBe([lojaA.Combinada, lojaA.Painel, lojaA.Totem], ignoreOrder: true);
    }

    [Fact]
    public async Task AdminDoCliente_403()
    {
        using var resposta = await PafApi.EnviarAsync(_tenantA.Admin, HttpMethod.Get, Rota, null, null);
        resposta.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Aviso_SoComEstacaoAtivaSemConexao_EmHorarioDeOperacao()
    {
        var agora = TimeOnly.FromDateTime(await AgoraEmSaoPauloAsync(_tenantA));
        var aberta = Janela.EmVoltaDe(agora);
        var fechada = Janela.ForaDe(agora);
        using var raiz = await RaizNoTenantAsync(_tenantA);

        var emOperacao = await CriarLojaAsync(_tenantA, aberta);
        var foraDoHorario = await CriarLojaAsync(_tenantA, fechada);
        var semFilaAtiva = await CriarLojaAsync(_tenantA, aberta);
        await DesativarFilasAsync(_tenantA, semFilaAtiva.UnidadeId);
        var semFila = await CriarLojaSemFilaAsync(_tenantA);
        await DesativarEstacaoAsync(_tenantA, emOperacao.UnidadeId, emOperacao.Painel);

        var papeis = (await ListarAsync(raiz)).Papeis;

        Linha(papeis, emOperacao.Totem).Aviso.ShouldBeTrue("ativa, nunca conectada, dentro da janela");
        Linha(papeis, emOperacao.Painel).Aviso.ShouldBeFalse("estação desativada não avisa");
        Linha(papeis, emOperacao.Painel).Conectada.ShouldBeFalse();
        Linha(papeis, foraDoHorario.Totem).Aviso.ShouldBeFalse("fora da janela de todas as filas");
        Linha(papeis, semFilaAtiva.Totem).Aviso.ShouldBeFalse("fila desativada não conta na união");
        Linha(papeis, semFila.Totem).Aviso.ShouldBeFalse("unidade sem fila ativa nunca avisa");

        var credencial = (await CredenciaisAsync(_tenantA, emOperacao.UnidadeId, emOperacao.Totem))[PafPapeisEstacao.Emissao];
        await ConectarAsync(_tenantA, credencial);
        Linha((await ListarAsync(raiz)).Papeis, emOperacao.Totem).Aviso.ShouldBeFalse("conectada não avisa");
    }

    private static PapelEstacaoMonitoradoDto Linha(IEnumerable<PapelEstacaoMonitoradoDto> papeis, Guid estacaoId) =>
        papeis.Single(p => p.EstacaoId == estacaoId);

    private static TimeSpan Validade => new OpcoesSessaoPaf().ValidadeEntrada;

    private Instancia NovaInstancia(RelogioDeTeste relogio)
    {
        var registro = new RegistroConexoes([_multiplexer], Options.Create(new OpcoesSessaoPaf()), relogio, NullLogger<RegistroConexoes>.Instance);
        var conexoes = new ConexoesPaf(factory.Services.GetRequiredService<IHubContext<PafHub>>(), registro, NullLogger<ConexoesPaf>.Instance);
        var monitoramento = new MonitoramentoEstacoes(conexoes, registro, factory.Services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<MonitoramentoEstacoes>.Instance);
        return new Instancia(registro, conexoes, monitoramento);
    }

    private async Task<PapelEstacaoMonitoradoDto> PapelPeloHandlerAsync(RegistroConexoes registro, Guid estacaoId) =>
        (await PapelEFonteAsync(registro, estacaoId)).Papel;

    private async Task<(PapelEstacaoMonitoradoDto Papel, bool PeloRegistro)> PapelEFonteAsync(RegistroConexoes registro, Guid estacaoId)
    {
        using var scope = factory.Services.CreateScope();
        var db = _tenantA.Contexto(scope, unidadeCorrente: null);
        var handler = new ListEstacoesOperadorQueryHandler(db, registro,
            scope.ServiceProvider.GetRequiredService<IMultiTenantContextAccessor<AppTenantInfo>>());
        var resultado = await handler.Handle(new ListEstacoesOperadorQuery(), CancellationToken.None);
        return (resultado.Papeis.Single(p => p.EstacaoId == estacaoId), resultado.EstadoPeloRegistro);
    }

    private static Task<MonitoramentoEstacoesDto> ListarAsync(HttpClient raiz) =>
        PafApi.OkAsync<MonitoramentoEstacoesDto>(raiz, HttpMethod.Get, Rota, null, null);

    private async Task<HttpClient> RaizNoTenantAsync(PafTenantCliente tenant)
    {
        var token = await new AuthHelper(factory).GetRootAdminTokenAsync();
        var cliente = factory.CreateClient();
        cliente.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
        cliente.DefaultRequestHeaders.Add("tenant", tenant.TenantId);
        return cliente;
    }

    private async Task<Loja> CriarLojaAsync(PafTenantCliente tenant, Janela janela)
    {
        using var scope = factory.Services.CreateScope();
        var db = tenant.Contexto(scope, unidadeCorrente: null);
        var unidade = Unidade.Criar($"Loja {Guid.NewGuid():N}", documentoFiscal: null);
        var u = unidade.Id;
        var caixa = Fila.Criar(u, "Caixa", "Caixa", setorId: null);
        var protocolo = Fila.Criar(u, "Protocolo", "Protocolo", setorId: null);
        caixa.DefinirJanela(janela.Inicio, janela.Fim);
        protocolo.DefinirJanela(janela.Inicio, janela.Fim);
        var loja = AdicionarEstacoes(db, unidade);
        db.Filas.AddRange(caixa, protocolo);
        await db.SaveChangesAsync();
        return loja;
    }

    private async Task<Loja> CriarLojaSemFilaAsync(PafTenantCliente tenant)
    {
        using var scope = factory.Services.CreateScope();
        var db = tenant.Contexto(scope, unidadeCorrente: null);
        var loja = AdicionarEstacoes(db, Unidade.Criar($"Loja {Guid.NewGuid():N}", documentoFiscal: null));
        await db.SaveChangesAsync();
        return loja;
    }

    private static Loja AdicionarEstacoes(PafDbContext db, Unidade unidade)
    {
        var u = unidade.Id;
        var totem = Estacao.Criar(u, "Totem", TipoEstacao.Emissao);
        var painel = Estacao.Criar(u, "Painel", TipoEstacao.Painel);
        var combinada = Estacao.Criar(u, "Combinada", TipoEstacao.EmissaoPainel);
        var guiche = Estacao.Criar(u, "Guichê", TipoEstacao.Guiche);
        db.Unidades.Add(unidade);
        db.Estacoes.AddRange(totem, painel, combinada, guiche, Estacao.Criar(u, "Administração", TipoEstacao.Administracao));
        db.ContratosUnidade.Add(ContratoUnidade.Criar(u, DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-2)), null, "Teste", 5, PoliticaLimiteLicenca.Recusar));
        return new Loja(u, totem.Id, painel.Id, combinada.Id, guiche.Id);
    }

    private async Task DesativarFilasAsync(PafTenantCliente tenant, Guid unidadeId)
    {
        using var scope = factory.Services.CreateScope();
        var db = tenant.Contexto(scope, unidadeId);
        foreach (var fila in await db.Filas.ToListAsync())
        {
            fila.Desativar();
        }

        await db.SaveChangesAsync();
    }

    private async Task DesativarEstacaoAsync(PafTenantCliente tenant, Guid unidadeId, Guid estacaoId)
    {
        using var scope = factory.Services.CreateScope();
        var db = tenant.Contexto(scope, unidadeId);
        (await db.Estacoes.SingleAsync(e => e.Id == estacaoId)).Desativar();
        await db.SaveChangesAsync();
    }

    /// <summary>One link redeemed once: a second link would replace the device and revoke the first credentials.</summary>
    private async Task<Dictionary<string, string>> CredenciaisAsync(PafTenantCliente tenant, Guid unidadeId, Guid estacaoId)
    {
        VinculoEstacaoGeradoDto link;
        using (var scope = factory.Services.CreateScope())
        {
            tenant.Contexto(scope, unidadeId);
            link = await scope.ServiceProvider.GetRequiredService<IMediator>().Send(new GerarVinculoEstacaoCommand(estacaoId));
        }

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("tenant", tenant.TenantId);
        using var resgate = await client.PostAsJsonAsync("/api/v1/paf/estacoes/vinculo/resgatar", new { token = link.Token });
        resgate.StatusCode.ShouldBe(HttpStatusCode.OK, await resgate.Content.ReadAsStringAsync());
        var credenciais = await resgate.Content.ReadFromJsonAsync<CredenciaisEstacaoDto>();
        return credenciais!.Credenciais.ToDictionary(c => c.Papel, c => c.AccessToken, StringComparer.Ordinal);
    }

    private async Task<HubConnection> ConectarAsync(PafTenantCliente tenant, string token)
    {
        var hub = await new PafOperacao(factory, tenant).ConectarAsync(token);
        _hubs.Add(hub);
        return hub;
    }

    private async Task DesconectarAsync(HubConnection hub)
    {
        _hubs.Remove(hub);
        await hub.DisposeAsync();
    }

    private async Task<Estacao> EstacaoAsync(PafTenantCliente tenant, Guid unidadeId, Guid estacaoId)
    {
        using var scope = factory.Services.CreateScope();
        var db = tenant.Contexto(scope, unidadeId);
        return await db.Estacoes.AsNoTracking().SingleAsync(e => e.Id == estacaoId);
    }

    // The disconnect reaches the hub after DisposeAsync returns (long polling sends the close on its own request).
    private async Task<Estacao> EsperarAsync(PafTenantCliente tenant, Guid unidadeId, Guid estacaoId, Func<Estacao, bool> condicao)
    {
        var limite = Stopwatch.StartNew();
        while (true)
        {
            var estacao = await EstacaoAsync(tenant, unidadeId, estacaoId);
            if (condicao(estacao))
            {
                return estacao;
            }

            limite.Elapsed.ShouldBeLessThan(Espera, "o evento de desconexão não chegou à coluna");
            await Task.Delay(50);
        }
    }

    private async Task<DateTimeOffset> AgoraDoBancoAsync(PafTenantCliente tenant)
    {
        using var scope = factory.Services.CreateScope();
        var db = tenant.Contexto(scope, unidadeCorrente: null);
        return (await db.Database.SqlQuery<DateTimeOffset>($"SELECT now() AS \"Value\"").ToListAsync()).Single();
    }

    private async Task<DateTime> AgoraEmSaoPauloAsync(PafTenantCliente tenant)
    {
        using var scope = factory.Services.CreateScope();
        return await PafRelogio.AgoraEmSaoPauloAsync(tenant.Contexto(scope, unidadeCorrente: null), CancellationToken.None);
    }

    private sealed record Loja(Guid UnidadeId, Guid Totem, Guid Painel, Guid Combinada, Guid Guiche);

    private sealed record Instancia(RegistroConexoes Registro, ConexoesPaf Conexoes, MonitoramentoEstacoes Monitoramento);

    /// <summary>Fila window used by a test unidade; windows never cross midnight (RF02).</summary>
    private sealed record Janela(TimeOnly? Inicio, TimeOnly? Fim)
    {
        public static Janela SemRestricao { get; } = new(null, null);

        // One hour each side, clamped to the day; the test runs well inside it.
        public static Janela EmVoltaDe(TimeOnly agora)
        {
            var inicio = agora.ToTimeSpan() - TimeSpan.FromHours(1);
            var fim = agora.ToTimeSpan() + TimeSpan.FromHours(1);
            return new(
                inicio < TimeSpan.Zero ? TimeOnly.MinValue : TimeOnly.FromTimeSpan(inicio),
                fim >= TimeSpan.FromDays(1) ? TimeOnly.MaxValue : TimeOnly.FromTimeSpan(fim));
        }

        public static Janela ForaDe(TimeOnly agora) =>
            agora.Hour >= 12 ? new(new TimeOnly(0, 0), new TimeOnly(1, 0)) : new(new TimeOnly(22, 0), new TimeOnly(23, 0));
    }

    private sealed class RelogioDeTeste : TimeProvider
    {
        private DateTimeOffset _agora = DateTimeOffset.UtcNow;

        public override DateTimeOffset GetUtcNow() => _agora;

        public void Avancar(TimeSpan quanto) => _agora += quanto;
    }

    private sealed class ContextoDeTeste(string connectionId) : HubCallerContext
    {
        public override string ConnectionId { get; } = connectionId;
        public override string? UserIdentifier => null;
        public override ClaimsPrincipal? User => new();
        public override IDictionary<object, object?> Items { get; } = new Dictionary<object, object?>();
        public override IFeatureCollection Features { get; } = new FeatureCollection();
        public override CancellationToken ConnectionAborted => CancellationToken.None;
        public override void Abort()
        {
        }
    }
}
