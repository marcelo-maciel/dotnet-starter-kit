using System.Diagnostics;
using FSH.Framework.Core.Exceptions;
using FSH.Modules.PAF.Contracts.v1.Sessao;
using FSH.Modules.PAF.Contracts.v1.TempoReal;
using FSH.Modules.PAF.Data;
using FSH.Modules.PAF.Domain;
using FSH.Modules.PAF.Escopo;
using FSH.Modules.PAF.Features.v1.Sessao.SetUnidadeCorrente;
using FSH.Modules.PAF.Sessao;
using FSH.Modules.PAF.TempoReal;
using Integration.Tests.Infrastructure;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;
using Testcontainers.Redis;

namespace Integration.Tests.Tests.PAF;

/// <summary>
/// Migração da licença na troca manual de unidade (S07, RF22, ARQ seção 9): o tratador real sobre o PostgreSQL do
/// harness e um Valkey real, montado à mão como em <see cref="PafCascataLicencaTests"/>, porque o host do harness
/// sobe sem Valkey e ali a troca é sempre 503. Cada caso de licença roda com o conjunto e no modo degradado.
/// </summary>
[Collection(FshCollectionDefinition.Name)]
public sealed class PafMigracaoLicencaTests(FshWebApplicationFactory factory) : IAsyncLifetime
{
    private static readonly TimeSpan Prazo = TimeSpan.FromMinutes(Licencas.PrazoMarcaMinutos);
    private static readonly TimeSpan EsperaAviso = TimeSpan.FromSeconds(10);
    private readonly RedisContainer _valkey = new RedisBuilder("valkey/valkey:9.1.0-alpine").Build();
    private readonly PafTenantCliente _tenant = new(factory, "paf-mg");
    private readonly List<HubConnection> _conexoes = [];
    private ConnectionMultiplexer _multiplexer = default!;

    public async Task InitializeAsync()
    {
        await Task.WhenAll(_valkey.StartAsync(), _tenant.InicializarAsync());
        var opcoes = ConfigurationOptions.Parse(_valkey.GetConnectionString());
        opcoes.AbortOnConnectFail = false;
        _multiplexer = await ConnectionMultiplexer.ConnectAsync(opcoes);
    }

    public async Task DisposeAsync()
    {
        foreach (var conexao in _conexoes)
        {
            await conexao.DisposeAsync();
        }

        await _multiplexer.DisposeAsync();
        await _valkey.DisposeAsync();
    }

    /// <summary>Como fica o modo: conjunto reconstruído decide; sem a marca de reconstrução, a contagem do PostgreSQL.</summary>
    public enum Modo
    {
        Conjunto,
        Degradado,
    }

    [Theory]
    [InlineData(Modo.Conjunto)]
    [InlineData(Modo.Degradado)]
    public async Task Aceita_LicencaSaiDaOrigemEntraNoDestino_GuicheSoltoEEstadoGravado(Modo modo)
    {
        await PrepararModoAsync(modo);
        var origem = await _tenant.CriarUnidadeAsync(limiteSessoes: 2);
        var destino = await _tenant.CriarUnidadeAsync(limiteSessoes: 2);
        var s = await SessaoAsync(origem.UnidadeId, origem.UnidadeId, destino.UnidadeId);
        var outra = await SessaoAsync(origem.UnidadeId, origem.UnidadeId);
        await VincularAsync(origem.GuicheA, s.SessaoId);

        await TrocarAsync(s, origem.UnidadeId, destino.UnidadeId);

        (await UnidadeDaLinhaAsync(s.SessaoId)).ShouldBe(destino.UnidadeId);
        (await Estado().LerUnidadeAsync(_tenant.TenantId, s.SessaoId, CancellationToken.None)).ShouldBe(destino.UnidadeId);
        (await VinculosAtivosAsync(s.SessaoId)).ShouldBe(0);
        // Origin -1 and destination +1, in the set too (degraded: restored right after the commit).
        (await MembrosAsync(origem.UnidadeId)).ShouldBe([ConjuntoLicencas.Membro(outra.SessaoId)]);
        (await MembrosAsync(destino.UnidadeId)).ShouldBe([ConjuntoLicencas.Membro(s.SessaoId)]);
        (await IndiceAsync(s.SessaoId)).ShouldBe(destino.UnidadeId.ToString());
        (await ExcedentesAsync(s.SessaoId)).ShouldBe(0);
    }

    [Theory]
    [InlineData(Modo.Conjunto)]
    [InlineData(Modo.Degradado)]
    public async Task DestinoCheio_Recusada409_LinhaConjuntoEstadoEGuicheIntactos(Modo modo)
    {
        await PrepararModoAsync(modo);
        var origem = await _tenant.CriarUnidadeAsync(limiteSessoes: 2);
        var destino = await _tenant.CriarUnidadeAsync(limiteSessoes: 1);
        var s = await SessaoAsync(origem.UnidadeId, origem.UnidadeId, destino.UnidadeId);
        var ocupante = await SessaoAsync(destino.UnidadeId, destino.UnidadeId);
        await VincularAsync(origem.GuicheA, s.SessaoId);

        var erro = await Should.ThrowAsync<CustomException>(() => TrocarAsync(s, origem.UnidadeId, destino.UnidadeId));

        erro.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        erro.Message.ShouldBe(Licencas.MensagemDestinoLimite);
        await IntactaAsync(s, origem, destino, [ConjuntoLicencas.Membro(ocupante.SessaoId)], modo);
    }

    [Fact]
    public async Task DestinoSemContrato_Recusada409ComMensagemPropria_NadaMuda()
    {
        await PrepararModoAsync(Modo.Conjunto);
        var origem = await _tenant.CriarUnidadeAsync(limiteSessoes: 2);
        var semContrato = await _tenant.CriarUnidadeAsync(limiteSessoes: null);
        var s = await SessaoAsync(origem.UnidadeId, origem.UnidadeId, semContrato.UnidadeId);
        await VincularAsync(origem.GuicheA, s.SessaoId);

        var erro = await Should.ThrowAsync<CustomException>(() => TrocarAsync(s, origem.UnidadeId, semContrato.UnidadeId));

        erro.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        erro.Message.ShouldBe(Licencas.MensagemDestinoSemContrato);
        await IntactaAsync(s, origem, semContrato, [], Modo.Conjunto);
    }

    [Fact]
    public async Task SemValkey_Recusada503_LinhaEGuicheIntactos()
    {
        var origem = await _tenant.CriarUnidadeAsync(limiteSessoes: 2);
        var destino = await _tenant.CriarUnidadeAsync(limiteSessoes: 2);
        var s = await SessaoAsync(origem.UnidadeId, origem.UnidadeId, destino.UnidadeId);
        await VincularAsync(origem.GuicheA, s.SessaoId);

        using var scope = factory.Services.CreateScope();
        var handler = Handler(scope, s, origem.UnidadeId, new ConjuntoLicencas([], Prazo, NullLogger<ConjuntoLicencas>.Instance),
            new EstadoSessao([], NullLogger<EstadoSessao>.Instance));
        var erro = await Should.ThrowAsync<CustomException>(
            () => handler.Handle(new SetUnidadeCorrenteCommand(destino.UnidadeId), CancellationToken.None).AsTask());

        erro.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        (await UnidadeDaLinhaAsync(s.SessaoId)).ShouldBe(origem.UnidadeId);
        (await VinculosAtivosAsync(s.SessaoId)).ShouldBe(1);
        (await ExcedentesAsync(s.SessaoId)).ShouldBe(0);
    }

    [Fact]
    public async Task AtendimentoEmCursoNoGuiche_Recusada409_MembroVoltaAOrigem()
    {
        await PrepararModoAsync(Modo.Conjunto);
        var origem = await _tenant.CriarUnidadeAsync(limiteSessoes: 2);
        var destino = await _tenant.CriarUnidadeAsync(limiteSessoes: 2);
        var s = await SessaoAsync(origem.UnidadeId, origem.UnidadeId, destino.UnidadeId);
        await VincularAsync(origem.GuicheA, s.SessaoId);
        await _tenant.ChamarNoGuicheAsync(origem);

        var erro = await Should.ThrowAsync<CustomException>(() => TrocarAsync(s, origem.UnidadeId, destino.UnidadeId));

        erro.Message.ShouldBe(VinculosGuiche.MensagemAtendimentoEmCurso);
        await IntactaAsync(s, origem, destino, [], Modo.Conjunto);
    }

    [Theory]
    [InlineData(Modo.Conjunto)]
    [InlineData(Modo.Degradado)]
    public async Task UltrapassagemContratada_Aceita_ComExcedenteRegistrado(Modo modo)
    {
        await PrepararModoAsync(modo);
        var origem = await _tenant.CriarUnidadeAsync(limiteSessoes: 2);
        var destino = await _tenant.CriarUnidadeAsync(limiteSessoes: 1);
        await _tenant.ExecutarAsync($"""
            UPDATE paf."ContratosUnidade" SET "Politica" = {nameof(PoliticaLimiteLicenca.UltrapassarComAviso)}
             WHERE "TenantId" = {_tenant.TenantId} AND "UnidadeId" = {destino.UnidadeId}
            """);
        var s = await SessaoAsync(origem.UnidadeId, origem.UnidadeId, destino.UnidadeId);
        await SessaoAsync(destino.UnidadeId, destino.UnidadeId);

        await TrocarAsync(s, origem.UnidadeId, destino.UnidadeId);

        (await UnidadeDaLinhaAsync(s.SessaoId)).ShouldBe(destino.UnidadeId);
        (await ExcedentesAsync(s.SessaoId)).ShouldBe(1);
        (await MembrosAsync(destino.UnidadeId)).Count.ShouldBe(2);
    }

    [Theory]
    [InlineData(Modo.Conjunto)]
    [InlineData(Modo.Degradado)]
    public async Task DoisUsuariosParaAUltimaVaga_UmAceito(Modo modo)
    {
        await PrepararModoAsync(modo);
        var a = await _tenant.CriarUnidadeAsync(limiteSessoes: 2);
        var b = await _tenant.CriarUnidadeAsync(limiteSessoes: 2);
        var destino = await _tenant.CriarUnidadeAsync(limiteSessoes: 1);
        var s1 = await SessaoAsync(a.UnidadeId, a.UnidadeId, destino.UnidadeId);
        var s2 = await SessaoAsync(b.UnidadeId, b.UnidadeId, destino.UnidadeId);

        var resultados = await Task.WhenAll(
            TentarTrocarAsync(s1, a.UnidadeId, destino.UnidadeId),
            TentarTrocarAsync(s2, b.UnidadeId, destino.UnidadeId));

        resultados.Count(r => r is null).ShouldBe(1, "uma troca aceita");
        resultados.Single(r => r is not null)!.Message.ShouldBe(Licencas.MensagemDestinoLimite);
        var linhas = new[] { await UnidadeDaLinhaAsync(s1.SessaoId), await UnidadeDaLinhaAsync(s2.SessaoId) };
        linhas.Count(u => u == destino.UnidadeId).ShouldBe(1);
        (await MembrosAsync(destino.UnidadeId)).Count.ShouldBe(1);
    }

    [Theory]
    [InlineData(Modo.Conjunto)]
    [InlineData(Modo.Degradado)]
    public async Task TrocasCruzadas_50Rodadas_SemDeadlock_ContagensFechando(Modo modo)
    {
        await PrepararModoAsync(modo);
        var u1 = await _tenant.CriarUnidadeAsync(limiteSessoes: 2);
        var u2 = await _tenant.CriarUnidadeAsync(limiteSessoes: 2);
        var sa = await SessaoAsync(u1.UnidadeId, u1.UnidadeId, u2.UnidadeId);
        var sb = await SessaoAsync(u2.UnidadeId, u1.UnidadeId, u2.UnidadeId);
        var (ondeA, ondeB) = (u1.UnidadeId, u2.UnidadeId);
        var relogio = Stopwatch.StartNew();

        for (var rodada = 0; rodada < 50; rodada++)
        {
            // Crossed: A goes U1→U2 while B goes U2→U1, each taking the two unit locks in the degraded mode.
            await Task.WhenAll(TrocarAsync(sa, ondeA, ondeB), TrocarAsync(sb, ondeB, ondeA));
            (ondeA, ondeB) = (ondeB, ondeA);
        }

        relogio.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(90));
        (await UnidadeDaLinhaAsync(sa.SessaoId)).ShouldBe(ondeA);
        (await UnidadeDaLinhaAsync(sb.SessaoId)).ShouldBe(ondeB);
        (await MembrosAsync(ondeA)).ShouldBe([ConjuntoLicencas.Membro(sa.SessaoId)]);
        (await MembrosAsync(ondeB)).ShouldBe([ConjuntoLicencas.Membro(sb.SessaoId)]);
    }

    [Fact]
    public async Task OutraAbaDaSessao_RecebeOAviso_EPassaAReceberAUnidadeNova()
    {
        await PrepararModoAsync(Modo.Conjunto);
        var origem = await _tenant.CriarUnidadeAsync(limiteSessoes: 5);
        var destino = await _tenant.CriarUnidadeAsync(limiteSessoes: 5);
        var usuario = await _tenant.CriarUsuarioAsync(origem.UnidadeId, destino.UnidadeId);
        var token = await _tenant.EntrarAsync(usuario);
        var s = new SessaoTeste(PafTenantCliente.SessaoDoToken(token.AccessToken), Guid.Parse(usuario.UserId));
        var abaQueTroca = await ConectarAsync(token.AccessToken);
        var outraAba = await ConectarAsync(token.AccessToken);
        using var avisos = new Caixa<UnidadeCorrenteAlterada>(outraAba, UnidadeCorrenteAlterada.Metodo);
        using var daOrigem = new Caixa<string>(outraAba, "TesteOrigem");
        using var doDestino = new Caixa<string>(outraAba, "TesteDestino");
        using var doDestinoNaPropria = new Caixa<string>(abaQueTroca, "TesteDestino");

        await TrocarAsync(s, origem.UnidadeId, destino.UnidadeId);

        (await avisos.EsperarAsync(a => a.UnidadeId == destino.UnidadeId, EsperaAviso)).ShouldNotBeNull();
        var hub = factory.Services.GetRequiredService<IHubContext<PafHub>>();
        await hub.Clients.Group(PafGrupos.Unidade(_tenant.TenantId, origem.UnidadeId)).SendAsync("TesteOrigem", "o");
        await hub.Clients.Group(PafGrupos.Unidade(_tenant.TenantId, destino.UnidadeId)).SendAsync("TesteDestino", "d");
        (await doDestino.EsperarAsync(_ => true, EsperaAviso)).ShouldBe("d");
        (await doDestinoNaPropria.EsperarAsync(_ => true, EsperaAviso)).ShouldBe("d");
        // Sent before the destination message on the same connection: had it been delivered, it would be here by now.
        daOrigem.Recebidos.ShouldBeEmpty();
    }

    private sealed record SessaoTeste(Guid SessaoId, Guid UsuarioId);

    private EstadoSessao Estado() => new([_multiplexer], NullLogger<EstadoSessao>.Instance);

    private ConjuntoLicencas Conjunto() => new([_multiplexer], Prazo, NullLogger<ConjuntoLicencas>.Instance);

    // Degraded = Valkey up but not rebuilt: the set answers "not rebuilt" and the PostgreSQL count decides, while
    // the unit state can still be written (seção 9). The set is seeded either way, so the restore is visible.
    private async Task PrepararModoAsync(Modo modo)
    {
        if (modo == Modo.Conjunto)
        {
            (await Conjunto().MarcarReconstruidoAsync(CancellationToken.None)).ShouldBeTrue();
        }
    }

    private SetUnidadeCorrenteCommandHandler Handler(
        IServiceScope scope, SessaoTeste s, Guid unidadeCorrente, ConjuntoLicencas conjunto, EstadoSessao estado)
    {
        var db = _tenant.Contexto(scope, unidadeCorrente);
        var sessao = new SessaoCorrente();
        sessao.Definir(_tenant.TenantId, s.SessaoId, s.UsuarioId, unidadeCorrente);
        var unidade = scope.ServiceProvider.GetRequiredService<IUnidadeCorrente>();
        var escopo = scope.ServiceProvider.GetRequiredService<EscopoUsuario>();
        var licencas = new Licencas(db, unidade, conjunto, escopo, NullLogger<Licencas>.Instance);
        return new SetUnidadeCorrenteCommandHandler(db, sessao, new ConcessaoCorrente(), unidade, estado, escopo, licencas,
            scope.ServiceProvider.GetRequiredService<AvisosTempoReal>(), factory.Services.GetRequiredService<ConexoesPaf>());
    }

    private async Task TrocarAsync(SessaoTeste s, Guid origem, Guid destino)
    {
        using var scope = factory.Services.CreateScope();
        await Handler(scope, s, origem, Conjunto(), Estado()).Handle(new SetUnidadeCorrenteCommand(destino), CancellationToken.None);
    }

    private async Task<CustomException?> TentarTrocarAsync(SessaoTeste s, Guid origem, Guid destino)
    {
        try
        {
            await TrocarAsync(s, origem, destino);
            return null;
        }
        catch (CustomException ex)
        {
            return ex;
        }
    }

    /// <summary>Sessão com marca viva na <paramref name="licenca"/> e membro no conjunto dela; escopo = <paramref name="escopo"/>.</summary>
    private async Task<SessaoTeste> SessaoAsync(Guid licenca, params Guid[] escopo)
    {
        var s = new SessaoTeste(Guid.NewGuid(), Guid.NewGuid());
        using (var scope = factory.Services.CreateScope())
        {
            var db = _tenant.Contexto(scope, unidadeCorrente: null);
            for (var ordem = 0; ordem < escopo.Length; ordem++)
            {
                db.UsuariosUnidade.Add(UsuarioUnidade.Criar(s.UsuarioId, escopo[ordem], ordem));
            }

            await db.SaveChangesAsync();
        }

        await _tenant.ExecutarAsync($"""
            INSERT INTO paf."AtividadesSessao" ("SessaoId", "UsuarioId", "UnidadeId", "UltimaAtividadeEm", "TenantId")
            VALUES ({s.SessaoId}, {s.UsuarioId}, {licenca}, now(), {_tenant.TenantId})
            """);
        (await Conjunto().RestaurarAsync(_tenant.TenantId, ConjuntoLicencas.Membro(s.SessaoId), licenca, Prazo, CancellationToken.None))
            .ShouldBeTrue();
        return s;
    }

    private async Task IntactaAsync(SessaoTeste s, UnidadeTeste origem, UnidadeTeste destino, string[] membrosDoDestino, Modo modo)
    {
        (await UnidadeDaLinhaAsync(s.SessaoId)).ShouldBe(origem.UnidadeId);
        (await Estado().LerUnidadeAsync(_tenant.TenantId, s.SessaoId, CancellationToken.None)).ShouldBeNull();
        (await VinculosAtivosAsync(s.SessaoId)).ShouldBe(1);
        (await ExcedentesAsync(s.SessaoId)).ShouldBe(0);
        (await MembrosAsync(origem.UnidadeId)).ShouldContain(ConjuntoLicencas.Membro(s.SessaoId), $"modo {modo}");
        (await MembrosAsync(destino.UnidadeId)).Order().ShouldBe(membrosDoDestino.Order());
        (await IndiceAsync(s.SessaoId)).ShouldBe(origem.UnidadeId.ToString());
    }

    private async Task<List<string>> MembrosAsync(Guid unidadeId) =>
        (await _multiplexer.GetDatabase().SortedSetRangeByRankAsync(ConjuntoLicencas.ChaveConjunto(_tenant.TenantId, unidadeId)))
            .Select(v => v.ToString())
            .ToList();

    private async Task<string?> IndiceAsync(Guid sessaoId) =>
        await _multiplexer.GetDatabase().StringGetAsync($"paf:{_tenant.TenantId}:sessao:{ConjuntoLicencas.Membro(sessaoId)}:licenca");

    private async Task<Guid?> UnidadeDaLinhaAsync(Guid sessaoId)
    {
        using var scope = factory.Services.CreateScope();
        var db = _tenant.Contexto(scope, unidadeCorrente: null);
        return await db.AtividadesSessao.AsNoTracking().Where(a => a.SessaoId == sessaoId).Select(a => (Guid?)a.UnidadeId).SingleOrDefaultAsync();
    }

    private async Task<int> ExcedentesAsync(Guid sessaoId)
    {
        using var scope = factory.Services.CreateScope();
        var db = _tenant.Contexto(scope, unidadeCorrente: null);
        return await db.ConsumosLicenca.AsNoTracking()
            .IgnoreQueryFilters([PafQueryFilters.Unidade])
            .CountAsync(c => c.SessaoId == sessaoId && c.Tipo == TipoConsumoLicenca.Excedente);
    }

    private async Task VincularAsync(Guid estacaoId, Guid sessaoId)
    {
        using var scope = factory.Services.CreateScope();
        var db = _tenant.Contexto(scope, unidadeCorrente: null);
        db.SessoesEstacao.Add(SessaoEstacao.CriarParaSessao(estacaoId, sessaoId));
        await db.SaveChangesAsync();
    }

    private async Task<int> VinculosAtivosAsync(Guid sessaoId)
    {
        using var scope = factory.Services.CreateScope();
        var db = _tenant.Contexto(scope, unidadeCorrente: null);
        return await db.SessoesEstacao.AsNoTracking().CountAsync(v => v.SessaoId == sessaoId && v.LiberadoEm == null);
    }

    private async Task<HubConnection> ConectarAsync(string token)
    {
        var conexao = new HubConnectionBuilder()
            .WithUrl(
                $"http://localhost{PafHubRota.Caminho}?tenant={Uri.EscapeDataString(_tenant.TenantId)}&access_token={Uri.EscapeDataString(token)}",
                options =>
                {
                    // Long polling: TestServer closes a WebSocket before the SignalR handshake (see PafHubTests).
                    options.Transports = HttpTransportType.LongPolling;
                    options.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
                })
            .Build();
        _conexoes.Add(conexao);
        await conexao.StartAsync();
        await PafOperacao.ProntaAsync(conexao);
        return conexao;
    }
}
