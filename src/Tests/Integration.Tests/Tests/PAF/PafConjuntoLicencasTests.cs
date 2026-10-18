using System.Diagnostics;
using Finbuckle.MultiTenant.Abstractions;
using FSH.Framework.Core.Exceptions;
using FSH.Framework.Shared.Multitenancy;
using FSH.Modules.Identity.Contracts.Services;
using FSH.Modules.PAF.Domain;
using FSH.Modules.PAF.Escopo;
using FSH.Modules.PAF.Sessao;
using Integration.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;
using Testcontainers.Redis;

namespace Integration.Tests.Tests.PAF;

/// <summary>
/// Licença com o conjunto no Valkey sobre o PostgreSQL do harness (seção 9, RF22): ocupação decidida pelo
/// conjunto e gravada em AtividadeSessao, modo degradado com o Valkey pausado, entrada antes da reconstrução e a
/// reconstrução a partir do banco. O host do harness sobe sem Valkey, então <see cref="Licencas"/> é montada à
/// mão com o contêiner deste teste.
/// </summary>
[Collection(FshCollectionDefinition.Name)]
public sealed class PafConjuntoLicencasTests(FshWebApplicationFactory factory) : IAsyncLifetime
{
    private static readonly TimeSpan Prazo = TimeSpan.FromMinutes(Licencas.PrazoMarcaMinutos);
    private readonly RedisContainer _valkey = new RedisBuilder("valkey/valkey:9.1.0-alpine").Build();
    private readonly PafTenantCliente _tenant = new(factory, "paf-cl");
    private ConnectionMultiplexer _multiplexer = default!;

    public async Task InitializeAsync()
    {
        await Task.WhenAll(_valkey.StartAsync(), _tenant.InicializarAsync());
        var opcoes = ConfigurationOptions.Parse(_valkey.GetConnectionString());
        opcoes.AllowAdmin = true;
        opcoes.AbortOnConnectFail = false;
        _multiplexer = await ConnectionMultiplexer.ConnectAsync(opcoes);
        (await Conjunto().MarcarReconstruidoAsync(CancellationToken.None)).ShouldBeTrue();
    }

    public async Task DisposeAsync()
    {
        await _multiplexer.DisposeAsync();
        await _valkey.DisposeAsync();
    }

    private ConjuntoLicencas Conjunto() => new([_multiplexer], Prazo, NullLogger<ConjuntoLicencas>.Instance);

    private Licencas NovasLicencas(IServiceScope scope) => new(
        _tenant.Contexto(scope, unidadeCorrente: null),
        scope.ServiceProvider.GetRequiredService<IUnidadeCorrente>(),
        Conjunto(),
        NullLogger<Licencas>.Instance);

    private async Task<List<string>> MembrosAsync(Guid unidadeId) =>
        (await _multiplexer.GetDatabase().SortedSetRangeByRankAsync(ConjuntoLicencas.ChaveConjunto(_tenant.TenantId, unidadeId)))
            .Select(v => v.ToString())
            .ToList();

    private async Task<Guid> UsuarioDoPafAsync(params Guid[] unidades)
    {
        var usuarioId = Guid.NewGuid();
        using var scope = factory.Services.CreateScope();
        var db = _tenant.Contexto(scope, unidadeCorrente: null);
        for (var ordem = 0; ordem < unidades.Length; ordem++)
        {
            db.UsuariosUnidade.Add(UsuarioUnidade.Criar(usuarioId, unidades[ordem], ordem));
        }

        await db.SaveChangesAsync();
        return usuarioId;
    }

    /// <summary>Login pelo caminho do comportamento: reserva, sessão nova, confirmação. Nulo quando recusado.</summary>
    private async Task<Guid?> EntrarAsync(Guid usuarioId)
    {
        using var scope = factory.Services.CreateScope();
        try
        {
            await using var reserva = await NovasLicencas(scope).ReservarAsync(_tenant.TenantId, usuarioId, CancellationToken.None);
            reserva.ShouldNotBeNull();
            var sessaoId = Guid.NewGuid();
            await reserva.ConfirmarAsync(sessaoId, [], CancellationToken.None);
            return sessaoId;
        }
        catch (ForbiddenException erro) when (erro.Message == Licencas.MensagemLimite)
        {
            return null;
        }
    }

    private async Task<List<Guid>> SessoesNoBancoAsync(Guid unidadeId)
    {
        using var scope = factory.Services.CreateScope();
        var db = _tenant.Contexto(scope, unidadeCorrente: null);
        return await db.AtividadesSessao.AsNoTracking().Where(a => a.UnidadeId == unidadeId).Select(a => a.SessaoId).ToListAsync();
    }

    [Fact]
    public async Task UltimaVaga_DezLoginsSimultaneos_UmAceitoNoConjuntoENoBanco()
    {
        var unidade = await _tenant.CriarUnidadeAsync(limiteSessoes: 1);
        var usuarios = new List<Guid>();
        for (var i = 0; i < 10; i++)
        {
            usuarios.Add(await UsuarioDoPafAsync(unidade.UnidadeId));
        }

        var sessoes = await Task.WhenAll(usuarios.Select(u => Task.Run(() => EntrarAsync(u))));

        var aceita = sessoes.Where(s => s is not null).ShouldHaveSingleItem()!.Value;
        (await MembrosAsync(unidade.UnidadeId)).ShouldBe([ConjuntoLicencas.Membro(aceita)]);
        (await SessoesNoBancoAsync(unidade.UnidadeId)).ShouldBe([aceita]);
    }

    [Fact]
    public async Task NovoLoginDoMesmoUsuario_NaUltimaVaga_TrocaAMarcaAnteriorPelaNova()
    {
        var unidade = await _tenant.CriarUnidadeAsync(limiteSessoes: 1);
        var usuario = await UsuarioDoPafAsync(unidade.UnidadeId);
        var primeira = await EntrarAsync(usuario);

        var segunda = await EntrarAsync(usuario);

        primeira.ShouldNotBeNull();
        segunda.ShouldNotBeNull();
        (await MembrosAsync(unidade.UnidadeId)).ShouldBe([ConjuntoLicencas.Membro(segunda.Value)]);
        (await SessoesNoBancoAsync(unidade.UnidadeId)).ShouldBe([segunda.Value]);
    }

    [Fact]
    public async Task ReservaDescartadaSemConfirmar_NaoDeixaMembroNemLinha()
    {
        var unidade = await _tenant.CriarUnidadeAsync(limiteSessoes: 1);
        var usuario = await UsuarioDoPafAsync(unidade.UnidadeId);
        using (var scope = factory.Services.CreateScope())
        {
            await using var reserva = await NovasLicencas(scope).ReservarAsync(_tenant.TenantId, usuario, CancellationToken.None);
            reserva.ShouldNotBeNull();
            (await MembrosAsync(unidade.UnidadeId)).ShouldHaveSingleItem().ShouldStartWith(ConjuntoLicencas.PrefixoReserva);
        }

        (await MembrosAsync(unidade.UnidadeId)).ShouldBeEmpty();
        (await SessoesNoBancoAsync(unidade.UnidadeId)).ShouldBeEmpty();
        (await EntrarAsync(await UsuarioDoPafAsync(unidade.UnidadeId))).ShouldNotBeNull();
    }

    [Fact]
    public async Task ReservaDescartadaComRequisicaoCancelada_SoltaOMembroSemTrocarOErro()
    {
        var unidade = await _tenant.CriarUnidadeAsync(limiteSessoes: 1);
        var usuario = await UsuarioDoPafAsync(unidade.UnidadeId);
        using var requisicao = new CancellationTokenSource();

        // The client drops and the token issue fails: that failure must surface, not a cancellation thrown by the
        // cleanup, and the provisional seat must not wait a minute to free itself.
        var erro = await Should.ThrowAsync<InvalidOperationException>(async () =>
        {
            using var scope = factory.Services.CreateScope();
            await using var reserva = await NovasLicencas(scope).ReservarAsync(_tenant.TenantId, usuario, requisicao.Token);
            await requisicao.CancelAsync();
            throw new InvalidOperationException("emissão do token falhou");
        });

        erro.Message.ShouldBe("emissão do token falhou");

        (await MembrosAsync(unidade.UnidadeId)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Logout_TiraOMembroEAbreAVaga()
    {
        var unidade = await _tenant.CriarUnidadeAsync(limiteSessoes: 1);
        var sessao = (await EntrarAsync(await UsuarioDoPafAsync(unidade.UnidadeId)))!.Value;
        using (var scope = factory.Services.CreateScope())
        {
            await NovasLicencas(scope).LiberarAsync(_tenant.TenantId, sessao, CancellationToken.None);
        }

        (await MembrosAsync(unidade.UnidadeId)).ShouldBeEmpty();
        (await EntrarAsync(await UsuarioDoPafAsync(unidade.UnidadeId))).ShouldNotBeNull();
    }

    [Fact]
    public async Task ValkeyPausado_ModoDegradado_UltimaVagaComOMesmoResultadoEPrazoCurto()
    {
        var unidade = await _tenant.CriarUnidadeAsync(limiteSessoes: 1);
        var usuarios = new List<Guid>();
        for (var i = 0; i < 5; i++)
        {
            usuarios.Add(await UsuarioDoPafAsync(unidade.UnidadeId));
        }

        await _valkey.PauseAsync();
        Guid?[] sessoes;
        var relogio = Stopwatch.StartNew();
        try
        {
            sessoes = await Task.WhenAll(usuarios.Select(u => Task.Run(() => EntrarAsync(u))));
            relogio.Stop();
        }
        finally
        {
            await _valkey.UnpauseAsync();
        }

        var aceita = sessoes.Where(s => s is not null).ShouldHaveSingleItem()!.Value;
        (await SessoesNoBancoAsync(unidade.UnidadeId)).ShouldBe([aceita]);
        // Five logins serialized by the unit lock, each paying at most the 500 ms Valkey deadline.
        relogio.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(8));
    }

    [Fact]
    public async Task ValkeyEsvaziado_AntesDaReconstrucao_ContaPeloBancoERepoeOMembro()
    {
        var unidade = await _tenant.CriarUnidadeAsync(limiteSessoes: 1);
        var primeira = (await EntrarAsync(await UsuarioDoPafAsync(unidade.UnidadeId)))!.Value;
        await _multiplexer.GetServers()[0].FlushAllDatabasesAsync();

        // The set is empty, but the PostgreSQL still holds the seat: the login is refused by the degraded count.
        (await EntrarAsync(await UsuarioDoPafAsync(unidade.UnidadeId))).ShouldBeNull();

        var outra = await _tenant.CriarUnidadeAsync(limiteSessoes: 1);
        var sessao = (await EntrarAsync(await UsuarioDoPafAsync(outra.UnidadeId)))!.Value;
        (await MembrosAsync(outra.UnidadeId)).ShouldBe([ConjuntoLicencas.Membro(sessao)]);
        (await MembrosAsync(unidade.UnidadeId)).ShouldNotContain(ConjuntoLicencas.Membro(primeira));
    }

    [Fact]
    public async Task RenovacaoAntesDaReconstrucao_VoltaAUnidadeDaLinha_NaoAPrincipal()
    {
        var principal = await _tenant.CriarUnidadeAsync(limiteSessoes: 5);
        var corrente = await _tenant.CriarUnidadeAsync(limiteSessoes: 5);
        var usuario = await UsuarioDoPafAsync(principal.UnidadeId, corrente.UnidadeId);
        var sessao = (await EntrarAsync(usuario))!.Value;
        // The session moved to its second unit (unit switch) and its mark is two minutes old.
        await _tenant.ExecutarAsync($"""
            UPDATE paf."AtividadesSessao" SET "UnidadeId" = {corrente.UnidadeId}, "UltimaAtividadeEm" = now() - interval '2 minutes'
             WHERE "SessaoId" = {sessao}
            """);
        await _multiplexer.GetServers()[0].FlushAllDatabasesAsync();

        using (var scope = factory.Services.CreateScope())
        {
            (await NovasLicencas(scope).RenovarMarcaAsync(_tenant.TenantId, sessao, CancellationToken.None)).ShouldBeTrue();
        }

        (await MembrosAsync(corrente.UnidadeId)).ShouldBe([ConjuntoLicencas.Membro(sessao)]);
        (await MembrosAsync(principal.UnidadeId)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Reconstrucao_DepoisDoFlushAll_SoSessoesFrescasENaoRevogadas()
    {
        var unidade = await _tenant.CriarUnidadeAsync(limiteSessoes: 5);
        var viva = await _tenant.CriarUsuarioAsync(unidade.UnidadeId);
        var revogada = await _tenant.CriarUsuarioAsync(unidade.UnidadeId);
        var velha = await _tenant.CriarUsuarioAsync(unidade.UnidadeId);
        // Real logins through the harness host (no Valkey there: degraded mode), so each has a real UserSession.
        var sessaoViva = PafTenantCliente.SessaoDoToken((await _tenant.EntrarAsync(viva)).AccessToken);
        var sessaoRevogada = PafTenantCliente.SessaoDoToken((await _tenant.EntrarAsync(revogada)).AccessToken);
        var sessaoVelha = PafTenantCliente.SessaoDoToken((await _tenant.EntrarAsync(velha)).AccessToken);
        using (var scope = factory.Services.CreateScope())
        {
            _tenant.Contexto(scope, unidadeCorrente: null);
            // Revoked in the kit, which leaves the PAF row in place.
            (await scope.ServiceProvider.GetRequiredService<ISessionService>()
                .RevokeSessionForAdminAsync(sessaoRevogada, revogada.UserId, "teste", CancellationToken.None)).ShouldBeTrue();
        }

        await _tenant.ExecutarAsync($"""
            UPDATE paf."AtividadesSessao" SET "UltimaAtividadeEm" = now() - interval '11 minutes' WHERE "SessaoId" = {sessaoVelha}
            """);
        await _multiplexer.GetServers()[0].FlushAllDatabasesAsync();
        var conjunto = Conjunto();
        var reconstrucao = new ReconstrucaoLicencas(
            factory.Services.GetRequiredService<IServiceScopeFactory>(), conjunto, TimeSpan.FromMilliseconds(200),
            NullLogger<ReconstrucaoLicencas>.Instance);

        using var parar = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await reconstrucao.StartAsync(parar.Token);
        try
        {
            while (await conjunto.ReconstruidoAsync(parar.Token) is not true)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100), parar.Token);
            }
        }
        finally
        {
            await reconstrucao.StopAsync(CancellationToken.None);
        }

        (await MembrosAsync(unidade.UnidadeId)).ShouldBe([ConjuntoLicencas.Membro(sessaoViva)]);
        var restante = await _multiplexer.GetDatabase().SortedSetScoreAsync(
            ConjuntoLicencas.ChaveConjunto(_tenant.TenantId, unidade.UnidadeId), ConjuntoLicencas.Membro(sessaoViva));
        restante.ShouldNotBeNull();
    }

    [Fact]
    public async Task Reconstrucao_ComTenantDeBancoInalcancavel_ReconstroiOsDemaisEGravaAMarca()
    {
        var unidade = await _tenant.CriarUnidadeAsync(limiteSessoes: 5);
        var viva = await _tenant.CriarUsuarioAsync(unidade.UnidadeId);
        var sessaoViva = PafTenantCliente.SessaoDoToken((await _tenant.EntrarAsync(viva)).AccessToken);
        // Same shape as a tenant whose provisioning failed (TenantProvisioningFailureTests): in the store, database down.
        var quebrado = new AppTenantInfo(
            $"paf-qb-{Guid.NewGuid():N}"[..20], "PAF banco fora",
            "Host=127.0.0.1;Port=1;Database=does_not_exist;Username=postgres;Password=x;Timeout=3;Command Timeout=3",
            "qb@paf.test") { IsActive = true, ValidUpto = DateTime.UtcNow.AddMonths(1) };
        var loja = factory.Services.GetRequiredService<IMultiTenantStore<AppTenantInfo>>();
        (await loja.AddAsync(quebrado)).ShouldBeTrue();
        try
        {
            await _multiplexer.GetServers()[0].FlushAllDatabasesAsync();
            var conjunto = Conjunto();
            var reconstrucao = new ReconstrucaoLicencas(
                factory.Services.GetRequiredService<IServiceScopeFactory>(), conjunto, TimeSpan.FromMinutes(1),
                NullLogger<ReconstrucaoLicencas>.Instance);
            using var prazo = new CancellationTokenSource(TimeSpan.FromSeconds(30));

            (await reconstrucao.ReconstruirAsync(prazo.Token)).ShouldBeTrue("um tenant fora não pode deixar todos no modo degradado");
            (await conjunto.ReconstruidoAsync(prazo.Token)).ShouldBe(true);
            (await MembrosAsync(unidade.UnidadeId)).ShouldBe([ConjuntoLicencas.Membro(sessaoViva)]);
        }
        finally
        {
            (await loja.RemoveAsync(quebrado.Identifier)).ShouldBeTrue();
        }
    }
}
