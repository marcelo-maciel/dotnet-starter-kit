using System.Diagnostics;
using System.Net;
using System.Security.Claims;
using Finbuckle.MultiTenant.Abstractions;
using FSH.Framework.Shared.Multitenancy;
using FSH.Modules.PAF.Contracts.v1.TempoReal;
using FSH.Modules.PAF.Sessao;
using FSH.Modules.PAF.TempoReal;
using Integration.Tests.Infrastructure;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using Testcontainers.Redis;

namespace Integration.Tests.Tests.PAF;

/// <summary>
/// Registro de conexões no Valkey e vida da sessão pela conexão (seção 7, seção 9, RF22), contra um Valkey real e o
/// PostgreSQL do harness. O host do harness sobe sem Valkey: as "instâncias" aqui são pares de RegistroConexoes e
/// ConexoesPaf montados sobre o mesmo Valkey e o IHubContext do host, com relógio simulado, e o ciclo de renovação é
/// chamado direto em vez de esperar o temporizador.
/// </summary>
[Collection(FshCollectionDefinition.Name)]
public sealed class PafRegistroConexoesTests(FshWebApplicationFactory factory) : IAsyncLifetime
{
    private static readonly TimeSpan Espera = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan SilencioEsperado = TimeSpan.FromSeconds(1.5);
    private static readonly TimeSpan Carencia = TimeSpan.FromMinutes(2);

    private readonly RedisContainer _valkey = new RedisBuilder("valkey/valkey:9.1.0-alpine").Build();
    private readonly PafTenantCliente _tenant = new(factory, "paf-rc");
    private readonly List<HubConnection> _hubs = [];
    private ConnectionMultiplexer _multiplexer = default!;
    private PafOperacao _op = default!;

    public async Task InitializeAsync()
    {
        await Task.WhenAll(_valkey.StartAsync(), _tenant.InicializarAsync());
        var opcoes = ConfigurationOptions.Parse(_valkey.GetConnectionString());
        // How the kit connects (Caching/Extensions.cs): a paused Valkey must not fail the multiplexer.
        opcoes.AbortOnConnectFail = false;
        _multiplexer = await ConnectionMultiplexer.ConnectAsync(opcoes);
        _op = new PafOperacao(factory, _tenant);
    }

    public async Task DisposeAsync()
    {
        foreach (var hub in _hubs)
        {
            await hub.DisposeAsync();
        }

        await _op.DrenarCaixaDeSaidaAsync();
        await _multiplexer.DisposeAsync();
        await _valkey.DisposeAsync();
    }

    [Fact]
    public async Task DuasInstancias_ASegundaAchaETiraDosGruposAConexaoDaPrimeira()
    {
        var c = await _op.CriarAsync();
        using var totem = await _op.TotemAsync(c);
        using var atendente = await _op.AtendenteAsync(c, c.GuicheA);
        var token = Token(atendente);
        var pessoa = Pessoa(token);
        var hub = await ConectarAsync(token);
        using var filas = new Caixa<FilaAtualizada>(hub, PafHubMetodos.FilaAtualizada);

        // Setup proof: the host put the person in the unit group, so the silence below is the removal's doing.
        await PafOperacao.EmitirOkAsync(totem, c.Caixa, c.Normal);
        (await filas.EsperarAsync(f => f.UnidadeId == c.UnidadeId, Espera)).ShouldNotBeNull();

        var relogio = new RelogioDeTeste();
        var primeira = NovaInstancia(relogio, Carencia);
        var segunda = NovaInstancia(relogio, Carencia);
        var contexto = new ContextoDeTeste(hub.ConnectionId!, Principal(token));
        await primeira.Conexoes.RegistrarAsync(contexto, pessoa, CancellationToken.None);
        foreach (var grupo in new[]
                 {
                     PafGrupos.Unidade(_tenant.TenantId, c.UnidadeId),
                     PafGrupos.Usuario(_tenant.TenantId, pessoa.UsuarioId),
                     PafGrupos.Guiche(_tenant.TenantId, c.GuicheA),
                 })
        {
            await primeira.Conexoes.EntrarNoGrupoAsync(contexto.ConnectionId, _tenant.TenantId, grupo, CancellationToken.None);
        }

        var alcancadas = await segunda.Conexoes.RemoverDeTodosOsGruposAsync(_tenant.TenantId, pessoa.SessaoId!.Value, CancellationToken.None);

        alcancadas.ShouldBe(1, "a segunda instância acha pelo registro a conexão que só a primeira segura");
        var recebidas = filas.Recebidos.Count;
        await PafOperacao.EmitirOkAsync(totem, c.Caixa, c.Normal);
        await Task.Delay(SilencioEsperado);
        filas.Recebidos.Count.ShouldBe(recebidas, "fora de todos os grupos, a conexão não recebe mais o movimento da unidade");

        (await segunda.Conexoes.EncerrarSessaoAsync(_tenant.TenantId, pessoa.SessaoId.Value, "teste", CancellationToken.None)).ShouldBe(1);
        contexto.Abortada.ShouldBeFalse("só a instância que segura a conexão consegue abortá-la");
        await primeira.Conexoes.EncerrarSessaoAsync(_tenant.TenantId, pessoa.SessaoId.Value, "teste", CancellationToken.None);
        contexto.Abortada.ShouldBeTrue();
    }

    [Fact]
    public async Task DuasInstancias_SessaoInvalidadaChegaPelaOutra_ERepeticaoNaoGeraSegundoAviso()
    {
        var c = await _op.CriarAsync();
        using var atendente = await _op.AtendenteAsync(c, c.GuicheA);
        var token = Token(atendente);
        var pessoa = Pessoa(token);
        var hub = await ConectarAsync(token);
        using var avisos = new Caixa<SessaoInvalidada>(hub, SessaoInvalidada.Metodo);

        var relogio = new RelogioDeTeste();
        var primeira = NovaInstancia(relogio, Carencia);
        var segunda = NovaInstancia(relogio, Carencia);
        var contexto = new ContextoDeTeste(hub.ConnectionId!, Principal(token));
        await primeira.Conexoes.RegistrarAsync(contexto, pessoa, CancellationToken.None);

        // The PAF-born revocation on one instance, then the kit event's copy handled on another: both reach the
        // connection the first instance holds, which stays open (it ignores the notice) between them.
        (await segunda.Conexoes.EncerrarSessaoAsync(_tenant.TenantId, pessoa.SessaoId!.Value, "primeiro", CancellationToken.None)).ShouldBe(1);
        (await avisos.EsperarAsync(a => a.SessaoId == pessoa.SessaoId, Espera)).ShouldNotBeNull()
            .Motivo.ShouldBe("primeiro", "o aviso atravessa instâncias, enviado à conexão pelo id que o registro guarda");
        (await primeira.Conexoes.EncerrarSessaoAsync(_tenant.TenantId, pessoa.SessaoId.Value, "repetido", CancellationToken.None)).ShouldBe(1);
        await Task.Delay(SilencioEsperado);

        avisos.Recebidos.Count.ShouldBe(1, "o aviso repetido é descartado pelo id da sessão");
        contexto.Abortada.ShouldBeTrue("a repetição ainda fecha a conexão onde ela está");
    }

    [Fact]
    public async Task AvisoQueFalhaNoBackplane_NaoImpedeOFechamentoDaConexao()
    {
        var opcoes = Options.Create(new OpcoesSessaoPaf { CarenciaReconexao = Carencia });
        var semValkey = new RegistroConexoes([], opcoes, new RelogioDeTeste(), NullLogger<RegistroConexoes>.Instance);
        var conexoes = new ConexoesPaf(new HubQueNaoEntrega(), semValkey, NullLogger<ConexoesPaf>.Instance);
        var sessaoId = Guid.NewGuid();
        var contexto = new ContextoDeTeste($"conexao-{sessaoId:N}", new ClaimsPrincipal());
        await conexoes.RegistrarAsync(contexto, new ConexaoPessoa(_tenant.TenantId, Guid.NewGuid().ToString(), false, sessaoId, null), CancellationToken.None);

        (await conexoes.EncerrarSessaoAsync(_tenant.TenantId, sessaoId, "motivo", CancellationToken.None)).ShouldBe(1);

        contexto.Abortada.ShouldBeTrue("o aviso é a experiência da tela; a revogação fecha a conexão mesmo sem ele");
    }

    [Fact]
    public async Task InstanciaQueParaDeRenovar_SomeDoRegistroDepoisDaValidade()
    {
        var relogio = new RelogioDeTeste();
        var morta = NovaInstancia(relogio, Carencia);
        var viva = NovaInstancia(relogio, Carencia);
        var sessaoId = Guid.NewGuid();
        var pessoa = new ConexaoPessoa(_tenant.TenantId, Guid.NewGuid().ToString(), false, sessaoId, null);
        var estacao = new ConexaoEstacao(_tenant.TenantId, "Painel", Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var daPessoa = new ContextoDeTeste(Guid.NewGuid().ToString("N"), new ClaimsPrincipal());
        await morta.Conexoes.RegistrarAsync(daPessoa, pessoa, CancellationToken.None);
        await morta.Conexoes.RegistrarAsync(new ContextoDeTeste(Guid.NewGuid().ToString("N"), new ClaimsPrincipal()), estacao, CancellationToken.None);

        // While the holding instance renews, the entry outlives its validity many times over.
        for (var minuto = 0; minuto < 5; minuto++)
        {
            relogio.Avancar(TimeSpan.FromMinutes(1));
            await morta.Renovacao.CicloAsync(CancellationToken.None);
        }

        (await viva.Registro.DaSessaoAsync(_tenant.TenantId, sessaoId, CancellationToken.None)).ShouldBe([daPessoa.ConnectionId]);

        // The process dies: no OnDisconnected, no renewal.
        var validade = new OpcoesSessaoPaf().ValidadeEntrada;
        relogio.Avancar(validade - TimeSpan.FromSeconds(1));
        (await viva.Registro.DaSessaoAsync(_tenant.TenantId, sessaoId, CancellationToken.None))!.Count.ShouldBe(1);
        relogio.Avancar(TimeSpan.FromMinutes(1) + TimeSpan.FromSeconds(1));

        (await viva.Registro.DaSessaoAsync(_tenant.TenantId, sessaoId, CancellationToken.None)).ShouldBeEmpty();
        (await viva.Conexoes.EncerrarSessaoAsync(_tenant.TenantId, sessaoId, "teste", CancellationToken.None)).ShouldBe(0);
        (await viva.Conexoes.EncerrarEstacaoAsync(_tenant.TenantId, estacao.EstacaoId, CancellationToken.None)).ShouldBe(0);
    }

    [Fact]
    public async Task EstacaoNaOutraInstancia_EncerrarAchaPeloPapel()
    {
        var relogio = new RelogioDeTeste();
        var primeira = NovaInstancia(relogio, Carencia);
        var segunda = NovaInstancia(relogio, Carencia);
        var estacaoId = Guid.NewGuid();
        await primeira.Conexoes.RegistrarAsync(
            new ContextoDeTeste("painel-1", new ClaimsPrincipal()),
            new ConexaoEstacao(_tenant.TenantId, "Painel", estacaoId, Guid.NewGuid(), Guid.NewGuid()), CancellationToken.None);
        await primeira.Conexoes.RegistrarAsync(
            new ContextoDeTeste("totem-1", new ClaimsPrincipal()),
            new ConexaoEstacao(_tenant.TenantId, "Emissao", estacaoId, Guid.NewGuid(), Guid.NewGuid()), CancellationToken.None);
        await primeira.Conexoes.RegistrarAsync(
            new ContextoDeTeste("outra-estacao", new ClaimsPrincipal()),
            new ConexaoEstacao(_tenant.TenantId, "Painel", Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()), CancellationToken.None);

        (await segunda.Conexoes.EncerrarEstacaoAsync(_tenant.TenantId, estacaoId, CancellationToken.None)).ShouldBe(2);
        (await segunda.Conexoes.EncerrarEstacaoAsync("outro-tenant", estacaoId, CancellationToken.None))
            .ShouldBe(0, "o tenant está na chave: o mesmo id de estação em outro tenant não é alcançado");
    }

    [Fact]
    public async Task TelaAbertaSemHttpPorOnzeMinutos_MantemALicenca_ELoginNaUltimaVagaERecusado()
    {
        var unidade = await _tenant.CriarUnidadeAsync(limiteSessoes: 1);
        var dono = await _tenant.CriarUsuarioAsync(unidade.UnidadeId);
        var outro = await _tenant.CriarUsuarioAsync(unidade.UnidadeId);
        var token = (await _tenant.EntrarAsync(dono)).AccessToken;
        var sessaoId = PafTenantCliente.SessaoDoToken(token);
        var hub = await ConectarAsync(token);
        var renovacao = factory.Services.GetRequiredService<RenovacaoPelaConexao>();

        // Eleven minutes of simulated clock: each minute the mark ages one minute and the host's cycle runs.
        await PassarMinutosAsync(sessaoId, renovacao, 11);

        (await IdadeDaMarcaAsync(sessaoId)).ShouldNotBeNull().ShouldBeLessThan(TimeSpan.FromMinutes(1));
        using (var recusado = await _tenant.LoginAsync(outro.Email, outro.Password))
        {
            recusado.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
            (await recusado.Content.ReadAsStringAsync()).ShouldContain(Licencas.MensagemLimite);
        }

        // Counterfactual: the same eleven minutes with the screen closed let the mark expire and free the seat.
        await hub.DisposeAsync();
        _hubs.Remove(hub);
        await EsperarAsync(() => !factory.Services.GetRequiredService<ConexoesPaf>().Locais()
            .Any(l => l.Conexao is ConexaoPessoa p && p.SessaoId == sessaoId));
        await PassarMinutosAsync(sessaoId, renovacao, 11);

        (await IdadeDaMarcaAsync(sessaoId)).ShouldNotBeNull().ShouldBeGreaterThan(TimeSpan.FromMinutes(10));
        using var aceito = await _tenant.LoginAsync(outro.Email, outro.Password);
        aceito.StatusCode.ShouldBe(HttpStatusCode.OK, await aceito.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task TelaAbertaSemHttp_DevolveOMembroAoConjuntoDeLicencasJuntoComAMarca()
    {
        var unidade = await _tenant.CriarUnidadeAsync(limiteSessoes: 1);
        var dono = await _tenant.CriarUsuarioAsync(unidade.UnidadeId);
        var token = (await _tenant.EntrarAsync(dono)).AccessToken;
        var sessaoId = PafTenantCliente.SessaoDoToken(token);
        var conjunto = new ConjuntoLicencas([_multiplexer], TimeSpan.FromMinutes(10), NullLogger<ConjuntoLicencas>.Instance);
        var instancia = NovaInstanciaComConjunto(new RelogioDeTeste(), conjunto);
        await instancia.Conexoes.RegistrarAsync(
            new ContextoDeTeste(Guid.NewGuid().ToString("N"), Principal(token)), Pessoa(token), CancellationToken.None);
        var chave = ConjuntoLicencas.ChaveConjunto(_tenant.TenantId, unidade.UnidadeId);
        var membro = ConjuntoLicencas.Membro(sessaoId);

        // The harness host has no Valkey, so the login left the set empty: only the renewal can put the member there.
        (await _multiplexer.GetDatabase().SortedSetScoreAsync(chave, membro)).ShouldBeNull();

        await PassarMinutosAsync(sessaoId, instancia.Renovacao, 2);

        (await _multiplexer.GetDatabase().SortedSetScoreAsync(chave, membro))
            .ShouldNotBeNull("a renovação pela conexão renova também o membro do conjunto (S09), senão a vaga vence em 10 min com a tela aberta");
    }

    [Fact]
    public async Task QuedaSemVolta_SoltaLicencaEGuicheSoDepoisDaCarencia()
    {
        var (pessoa, contexto) = await AtendenteComGuicheAsync();
        var relogio = new RelogioDeTeste();
        var instancia = NovaInstancia(relogio, Carencia);
        await instancia.Conexoes.RegistrarAsync(contexto, pessoa, CancellationToken.None);
        await instancia.Conexoes.RemoverAsync(contexto.ConnectionId, CancellationToken.None);

        relogio.Avancar(Carencia - TimeSpan.FromSeconds(1));
        await instancia.Renovacao.CicloAsync(CancellationToken.None);
        (await LicencaAsync(pessoa.SessaoId!.Value)).ShouldBe(1, "dentro da carência a licença fica");
        (await GuichesAsync(pessoa.SessaoId.Value)).ShouldBe(1, "dentro da carência o guichê fica");

        relogio.Avancar(TimeSpan.FromSeconds(2));
        await instancia.Renovacao.CicloAsync(CancellationToken.None);
        (await LicencaAsync(pessoa.SessaoId.Value)).ShouldBe(0, "passada a carência sem volta, a licença é solta");
        (await GuichesAsync(pessoa.SessaoId.Value)).ShouldBe(0, "e o guichê também");
    }

    [Fact]
    public async Task VoltaDentroDaCarencia_EmOutraInstancia_NaoSolta()
    {
        var (pessoa, contexto) = await AtendenteComGuicheAsync();
        var relogio = new RelogioDeTeste();
        var antiga = NovaInstancia(relogio, Carencia);
        var nova = NovaInstancia(relogio, Carencia);
        await antiga.Conexoes.RegistrarAsync(contexto, pessoa, CancellationToken.None);
        await antiga.Conexoes.RemoverAsync(contexto.ConnectionId, CancellationToken.None);

        // Deploy: the screen reconnects to the other instance within the grace period.
        relogio.Avancar(TimeSpan.FromMinutes(1));
        await nova.Conexoes.RegistrarAsync(new ContextoDeTeste("reconectada", new ClaimsPrincipal()), pessoa, CancellationToken.None);
        relogio.Avancar(TimeSpan.FromMinutes(1));
        await nova.Renovacao.CicloAsync(CancellationToken.None);
        relogio.Avancar(TimeSpan.FromMinutes(1));
        await nova.Renovacao.CicloAsync(CancellationToken.None);
        await antiga.Renovacao.CicloAsync(CancellationToken.None);

        (await LicencaAsync(pessoa.SessaoId!.Value)).ShouldBe(1);
        (await GuichesAsync(pessoa.SessaoId.Value)).ShouldBe(1);
    }

    [Fact]
    public async Task CarenciaConfiguradaEmTrintaSegundos_SoltaEmTrintaSegundos()
    {
        var (pessoa, contexto) = await AtendenteComGuicheAsync();
        var relogio = new RelogioDeTeste();
        var instancia = NovaInstancia(relogio, TimeSpan.FromSeconds(30));
        await instancia.Conexoes.RegistrarAsync(contexto, pessoa, CancellationToken.None);
        await instancia.Conexoes.RemoverAsync(contexto.ConnectionId, CancellationToken.None);

        relogio.Avancar(TimeSpan.FromSeconds(29));
        await instancia.Renovacao.CicloAsync(CancellationToken.None);
        (await LicencaAsync(pessoa.SessaoId!.Value)).ShouldBe(1);

        relogio.Avancar(TimeSpan.FromSeconds(2));
        await instancia.Renovacao.CicloAsync(CancellationToken.None);
        (await LicencaAsync(pessoa.SessaoId.Value)).ShouldBe(0, "a carência configurada vale, não os 2 minutos iniciais");
        (await GuichesAsync(pessoa.SessaoId.Value)).ShouldBe(0);
    }

    [Fact]
    public async Task CinquentaConexoesEQuedasParalelas_TerminamComContagemCerta()
    {
        var relogio = new RelogioDeTeste();
        var instancias = new[] { NovaInstancia(relogio, Carencia), NovaInstancia(relogio, Carencia) };
        var sessaoId = Guid.NewGuid();
        var pessoa = new ConexaoPessoa(_tenant.TenantId, Guid.NewGuid().ToString(), false, sessaoId, null);
        var conexoes = Enumerable.Range(0, 50).Select(n => (Id: $"c{n}", Registro: instancias[n % 2].Registro)).ToList();

        var registradas = await Task.WhenAll(conexoes.Select(c => c.Registro.RegistrarAsync(c.Id, pessoa, CancellationToken.None)));
        registradas.ShouldAllBe(ok => ok);
        (await instancias[0].Registro.DaSessaoAsync(_tenant.TenantId, sessaoId, CancellationToken.None))!.Count.ShouldBe(50);

        var primeiraMetade = await Task.WhenAll(conexoes.Take(25).Select(c => c.Registro.RemoverAsync(c.Id, pessoa, CancellationToken.None)));
        primeiraMetade.ShouldAllBe(ultima => !ultima, "restam conexões vivas: nenhuma queda é a última");
        (await instancias[1].Registro.DaSessaoAsync(_tenant.TenantId, sessaoId, CancellationToken.None))!.Count.ShouldBe(25);
        (await Queda(sessaoId)).ShouldBeNull();

        var segundaMetade = await Task.WhenAll(conexoes.Skip(25).Select(c => c.Registro.RemoverAsync(c.Id, pessoa, CancellationToken.None)));
        segundaMetade.Count(ultima => ultima).ShouldBe(1, "exatamente uma queda é a última e grava o instante");
        (await instancias[0].Registro.DaSessaoAsync(_tenant.TenantId, sessaoId, CancellationToken.None)).ShouldBeEmpty();
        (await Queda(sessaoId)).ShouldNotBeNull();
    }

    [Fact]
    public async Task ValkeyPausado_ConexaoAceitaDentroDoPrazo_EAMarcaSegueRenovada()
    {
        var (pessoa, contexto) = await AtendenteComGuicheAsync();
        var sessaoId = pessoa.SessaoId!.Value;
        var instancia = NovaInstancia(new RelogioDeTeste(), Carencia);
        await EnvelhecerMarcaAsync(sessaoId, TimeSpan.FromMinutes(3));

        await _valkey.PauseAsync();
        try
        {
            var relogio = Stopwatch.StartNew();
            await instancia.Conexoes.RegistrarAsync(contexto, pessoa, CancellationToken.None);
            relogio.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(2), "a chamada ao Valkey tem prazo curto");
            instancia.Conexoes.Locais().Select(l => l.ConnectionId).ShouldContain(contexto.ConnectionId);

            relogio.Restart();
            await instancia.Renovacao.CicloAsync(CancellationToken.None);
            relogio.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(5));
            (await IdadeDaMarcaAsync(sessaoId)).ShouldNotBeNull().ShouldBeLessThan(TimeSpan.FromMinutes(1),
                "a marca é renovada no PostgreSQL mesmo com o Valkey fora");

            // Without Valkey a drop is not released by the grace period: other instances' connections are invisible.
            await instancia.Conexoes.RemoverAsync(contexto.ConnectionId, CancellationToken.None);
            await instancia.Renovacao.CicloAsync(CancellationToken.None);
            (await LicencaAsync(sessaoId)).ShouldBe(1);
        }
        finally
        {
            await _valkey.UnpauseAsync();
        }
    }

    [Fact]
    public async Task TenantComBancoFora_NaoParaARenovacaoNemASolturaDosOutros()
    {
        var (caida, contextoCaida) = await AtendenteComGuicheAsync();
        var unidade = await _tenant.CriarUnidadeAsync(limiteSessoes: 5);
        var token = (await _tenant.EntrarAsync(await _tenant.CriarUsuarioAsync(unidade.UnidadeId))).AccessToken;
        var viva = Pessoa(token);
        // Same shape as a tenant whose provisioning failed (TenantProvisioningFailureTests): in the store, database down.
        var quebrado = new AppTenantInfo(
            $"paf-qb-{Guid.NewGuid():N}"[..20], "PAF banco fora",
            "Host=127.0.0.1;Port=1;Database=does_not_exist;Username=postgres;Password=x;Timeout=3;Command Timeout=3",
            "qb@paf.test") { IsActive = true, ValidUpto = DateTime.UtcNow.AddMonths(1) };
        var loja = factory.Services.GetRequiredService<IMultiTenantStore<AppTenantInfo>>();
        (await loja.AddAsync(quebrado)).ShouldBeTrue();
        try
        {
            var relogio = new RelogioDeTeste();
            var instancia = NovaInstancia(relogio, Carencia);
            // The broken tenant has a live connection (renewal) and a drop (release); both throw on its database.
            await instancia.Conexoes.RegistrarAsync(
                new ContextoDeTeste(Guid.NewGuid().ToString("N"), Principal(token)),
                viva with { TenantId = quebrado.Id, SessaoId = Guid.NewGuid() }, CancellationToken.None);
            var contextoQuebradoCaida = new ContextoDeTeste(Guid.NewGuid().ToString("N"), Principal(token));
            await instancia.Conexoes.RegistrarAsync(
                contextoQuebradoCaida, viva with { TenantId = quebrado.Id, SessaoId = Guid.NewGuid() }, CancellationToken.None);
            await instancia.Conexoes.RemoverAsync(contextoQuebradoCaida.ConnectionId, CancellationToken.None);
            // The drops are read by score: the broken tenant's comes first, so the release below runs after its failure.
            relogio.Avancar(TimeSpan.FromSeconds(1));
            await instancia.Conexoes.RegistrarAsync(
                new ContextoDeTeste(Guid.NewGuid().ToString("N"), Principal(token)), viva, CancellationToken.None);
            await instancia.Conexoes.RegistrarAsync(contextoCaida, caida, CancellationToken.None);
            await instancia.Conexoes.RemoverAsync(contextoCaida.ConnectionId, CancellationToken.None);
            await EnvelhecerMarcaAsync(viva.SessaoId!.Value, TimeSpan.FromMinutes(3));
            relogio.Avancar(Carencia + TimeSpan.FromSeconds(1));

            await Should.NotThrowAsync(() => instancia.Renovacao.CicloAsync(CancellationToken.None));

            (await IdadeDaMarcaAsync(viva.SessaoId.Value)).ShouldNotBeNull().ShouldBeLessThan(TimeSpan.FromMinutes(1),
                "o tenant com banco fora não pode parar a renovação dos outros");
            (await LicencaAsync(caida.SessaoId!.Value)).ShouldBe(0, "nem a soltura pela carência dos outros");
            (await GuichesAsync(caida.SessaoId.Value)).ShouldBe(0);
        }
        finally
        {
            (await loja.RemoveAsync(quebrado.Identifier)).ShouldBeTrue();
        }
    }

    [Fact]
    public async Task Guiche_RecebeFilaEAtendimentoDaUnidade_EOutraUnidadeNao()
    {
        var c = await _op.CriarAsync();
        var outra = await _op.CriarAsync();
        using var totem = await _op.TotemAsync(c);
        using var atendenteA = await _op.AtendenteAsync(c, c.GuicheA);
        using var atendenteB = await _op.AtendenteAsync(c, c.GuicheB);
        using var deOutraUnidade = await _op.AtendenteAsync(outra, outra.GuicheA);
        var hubA = await ConectarAsync(Token(atendenteA));
        var hubOutra = await ConectarAsync(Token(deOutraUnidade));
        using var filasA = new Caixa<FilaAtualizada>(hubA, PafHubMetodos.FilaAtualizada);
        using var atendimentosA = new Caixa<AtendimentoAtualizado>(hubA, PafHubMetodos.AtendimentoAtualizado);
        using var filasOutra = new Caixa<FilaAtualizada>(hubOutra, PafHubMetodos.FilaAtualizada);

        await PafOperacao.EmitirOkAsync(totem, c.Caixa, c.Normal);
        (await filasA.EsperarAsync(f => f.UnidadeId == c.UnidadeId && f.FilaId == c.Caixa, Espera))
            .ShouldNotBeNull("a pessoa entra no grupo da unidade da sessão ao conectar");

        await _op.EnvelhecerFichasAsync(c.UnidadeId);
        var chamada = await PafOperacao.ChamarOkAsync(atendenteB);
        await PafOperacao.AgirOkAsync(atendenteB, chamada.AtendimentoId, "iniciar");
        (await atendimentosA.EsperarAsync(a => a.AtendimentoId == chamada.AtendimentoId, Espera))
            .ShouldNotBeNull("o atendimento de outro guichê da unidade chega pelo grupo da unidade");

        await Task.Delay(SilencioEsperado);
        filasOutra.Recebidos.ShouldBeEmpty("o movimento da unidade não chega à pessoa de outra unidade");
    }

    // ─── helpers ─────────────────────────────────────────────────────

    private Instancia NovaInstancia(RelogioDeTeste relogio, TimeSpan carencia)
    {
        var opcoes = Options.Create(new OpcoesSessaoPaf { CarenciaReconexao = carencia });
        var registro = new RegistroConexoes([_multiplexer], opcoes, relogio, NullLogger<RegistroConexoes>.Instance);
        var conexoes = new ConexoesPaf(factory.Services.GetRequiredService<IHubContext<PafHub>>(), registro, NullLogger<ConexoesPaf>.Instance);
        var renovacao = new RenovacaoPelaConexao(
            conexoes, registro, factory.Services.GetRequiredService<IServiceScopeFactory>(), opcoes, relogio,
            NullLogger<RenovacaoPelaConexao>.Instance);
        return new Instancia(registro, conexoes, renovacao);
    }

    private Instancia NovaInstanciaComConjunto(RelogioDeTeste relogio, ConjuntoLicencas conjunto)
    {
        var opcoes = Options.Create(new OpcoesSessaoPaf { CarenciaReconexao = Carencia });
        var registro = new RegistroConexoes([_multiplexer], opcoes, relogio, NullLogger<RegistroConexoes>.Instance);
        var conexoes = new ConexoesPaf(factory.Services.GetRequiredService<IHubContext<PafHub>>(), registro, NullLogger<ConexoesPaf>.Instance);
        var escopos = new EscoposComConjunto(factory.Services.GetRequiredService<IServiceScopeFactory>(), conjunto);
        var renovacao = new RenovacaoPelaConexao(conexoes, registro, escopos, opcoes, relogio, NullLogger<RenovacaoPelaConexao>.Instance);
        return new Instancia(registro, conexoes, renovacao);
    }

    private async Task<(ConexaoPessoa Pessoa, ContextoDeTeste Contexto)> AtendenteComGuicheAsync()
    {
        var c = await _op.CriarAsync();
        using var atendente = await _op.AtendenteAsync(c, c.GuicheA);
        var token = Token(atendente);
        var pessoa = Pessoa(token);
        (await LicencaAsync(pessoa.SessaoId!.Value)).ShouldBe(1);
        (await GuichesAsync(pessoa.SessaoId.Value)).ShouldBe(1);
        return (pessoa, new ContextoDeTeste(Guid.NewGuid().ToString("N"), Principal(token)));
    }

    private async Task PassarMinutosAsync(Guid sessaoId, RenovacaoPelaConexao renovacao, int minutos)
    {
        for (var minuto = 0; minuto < minutos; minuto++)
        {
            await EnvelhecerMarcaAsync(sessaoId, TimeSpan.FromMinutes(1));
            await renovacao.CicloAsync(CancellationToken.None);
        }
    }

    private Task<int> EnvelhecerMarcaAsync(Guid sessaoId, TimeSpan quanto) =>
        _tenant.ExecutarAsync($"""
            UPDATE paf."AtividadesSessao" SET "UltimaAtividadeEm" = "UltimaAtividadeEm" - make_interval(secs => {quanto.TotalSeconds})
             WHERE "TenantId" = {_tenant.TenantId} AND "SessaoId" = {sessaoId}
            """);

    private async Task<TimeSpan?> IdadeDaMarcaAsync(Guid sessaoId)
    {
        using var scope = factory.Services.CreateScope();
        var db = _tenant.Contexto(scope, unidadeCorrente: null);
        var segundos = await db.Database.SqlQuery<double?>($"""
            SELECT EXTRACT(EPOCH FROM now() - "UltimaAtividadeEm")::float8 AS "Value"
              FROM paf."AtividadesSessao" WHERE "TenantId" = {_tenant.TenantId} AND "SessaoId" = {sessaoId}
            """).FirstOrDefaultAsync();
        return segundos is { } s ? TimeSpan.FromSeconds(s) : null;
    }

    private async Task<int> LicencaAsync(Guid sessaoId)
    {
        using var scope = factory.Services.CreateScope();
        var db = _tenant.Contexto(scope, unidadeCorrente: null);
        return await db.AtividadesSessao.CountAsync(a => a.SessaoId == sessaoId);
    }

    private async Task<int> GuichesAsync(Guid sessaoId)
    {
        using var scope = factory.Services.CreateScope();
        var db = _tenant.Contexto(scope, unidadeCorrente: null);
        return await db.SessoesEstacao.CountAsync(s => s.SessaoId == sessaoId && s.LiberadoEm == null);
    }

    private Task<double?> Queda(Guid sessaoId) =>
        _multiplexer.GetDatabase().SortedSetScoreAsync(RegistroConexoes.ChaveQuedas, $"{_tenant.TenantId}|{sessaoId:D}");

    private static async Task EsperarAsync(Func<bool> condicao)
    {
        var limite = Stopwatch.StartNew();
        while (!condicao())
        {
            limite.Elapsed.ShouldBeLessThan(Espera);
            await Task.Delay(50);
        }
    }

    private async Task<HubConnection> ConectarAsync(string token)
    {
        var hub = await _op.ConectarAsync(token);
        _hubs.Add(hub);
        return hub;
    }

    private static string Token(HttpClient cliente) => cliente.DefaultRequestHeaders.Authorization!.Parameter!;

    private static ConexaoPessoa Pessoa(string token) => (ConexaoPessoa)ConexaoPaf.Ler(Principal(token))!;

    private static ClaimsPrincipal Principal(string token) =>
        new(new ClaimsIdentity(
            new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler().ReadJwtToken(token).Claims,
            authenticationType: "Bearer"));

    private sealed record Instancia(RegistroConexoes Registro, ConexoesPaf Conexoes, RenovacaoPelaConexao Renovacao);

    // The harness host runs without Valkey; this hands the renewal a licence set that has one, the rest from the host.
    private sealed class EscoposComConjunto(IServiceScopeFactory host, ConjuntoLicencas conjunto) : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => new Escopo(host.CreateScope(), conjunto);

        private sealed class Escopo(IServiceScope doHost, ConjuntoLicencas conjunto) : IServiceScope, IServiceProvider
        {
            public IServiceProvider ServiceProvider => this;

            public object? GetService(Type serviceType) =>
                serviceType == typeof(ConjuntoLicencas) ? conjunto : doHost.ServiceProvider.GetService(serviceType);

            public void Dispose() => doHost.Dispose();
        }
    }

    private sealed class RelogioDeTeste : TimeProvider
    {
        private DateTimeOffset _agora = DateTimeOffset.UtcNow;

        public override DateTimeOffset GetUtcNow() => _agora;

        public void Avancar(TimeSpan quanto) => _agora += quanto;
    }

    private sealed class ContextoDeTeste(string connectionId, ClaimsPrincipal principal) : HubCallerContext
    {
        public bool Abortada { get; private set; }
        public override string ConnectionId { get; } = connectionId;
        public override string? UserIdentifier => null;
        public override ClaimsPrincipal? User => principal;
        public override IDictionary<object, object?> Items { get; } = new Dictionary<object, object?>();
        public override IFeatureCollection Features { get; } = new FeatureCollection();
        public override CancellationToken ConnectionAborted => CancellationToken.None;
        public override void Abort() => Abortada = true;
    }

    // A backplane that fails every send, as a stalled Valkey would past the ceiling; groups are never reached here.
    private sealed class HubQueNaoEntrega : IHubContext<PafHub>, IHubClients, IClientProxy
    {
        public IHubClients Clients => this;
        public IGroupManager Groups => throw new NotSupportedException("o teste não passa por grupos");
        IClientProxy IHubClients<IClientProxy>.All => this;
        IClientProxy IHubClients<IClientProxy>.AllExcept(IReadOnlyList<string> excludedConnectionIds) => this;
        IClientProxy IHubClients<IClientProxy>.Client(string connectionId) => this;
        IClientProxy IHubClients<IClientProxy>.Clients(IReadOnlyList<string> connectionIds) => this;
        IClientProxy IHubClients<IClientProxy>.Group(string groupName) => this;
        IClientProxy IHubClients<IClientProxy>.GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => this;
        IClientProxy IHubClients<IClientProxy>.Groups(IReadOnlyList<string> groupNames) => this;
        IClientProxy IHubClients<IClientProxy>.User(string userId) => this;
        IClientProxy IHubClients<IClientProxy>.Users(IReadOnlyList<string> userIds) => this;

        public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default) =>
            Task.FromException(new TimeoutException("backplane parado"));
    }
}
