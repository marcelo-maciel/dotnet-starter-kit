using System.Diagnostics;
using FSH.Framework.Core.Exceptions;
using FSH.Modules.PAF.Domain;
using FSH.Modules.PAF.Escopo;
using FSH.Modules.PAF.Sessao;
using Integration.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;
using Testcontainers.Redis;

namespace Integration.Tests.Tests.PAF;

/// <summary>
/// Cascata de licença pelo escopo efetivo (S05, RF22, ARQ seção 9): cada caso roda com o conjunto no Valkey e no
/// modo degradado (sem Valkey), com o mesmo resultado. <see cref="Licencas"/> é montada à mão, como em
/// <see cref="PafConjuntoLicencasTests"/>, porque o host do harness sobe sem Valkey.
/// </summary>
[Collection(FshCollectionDefinition.Name)]
public sealed class PafCascataLicencaTests(FshWebApplicationFactory factory) : IAsyncLifetime
{
    private static readonly TimeSpan Prazo = TimeSpan.FromMinutes(Licencas.PrazoMarcaMinutos);
    private readonly RedisContainer _valkey = new RedisBuilder("valkey/valkey:9.1.0-alpine").Build();
    private readonly PafTenantCliente _tenant = new(factory, "paf-cs");
    private ConnectionMultiplexer _multiplexer = default!;

    public async Task InitializeAsync()
    {
        await Task.WhenAll(_valkey.StartAsync(), _tenant.InicializarAsync());
        var opcoes = ConfigurationOptions.Parse(_valkey.GetConnectionString());
        opcoes.AbortOnConnectFail = false;
        _multiplexer = await ConnectionMultiplexer.ConnectAsync(opcoes);
        (await Conjunto(comValkey: true).MarcarReconstruidoAsync(CancellationToken.None)).ShouldBeTrue();
    }

    public async Task DisposeAsync()
    {
        await _multiplexer.DisposeAsync();
        await _valkey.DisposeAsync();
    }

    private ConjuntoLicencas Conjunto(bool comValkey) =>
        new(comValkey ? [_multiplexer] : [], Prazo, NullLogger<ConjuntoLicencas>.Instance);

    private Licencas NovasLicencas(IServiceScope scope, bool comValkey) => new(
        _tenant.Contexto(scope, unidadeCorrente: null),
        scope.ServiceProvider.GetRequiredService<IUnidadeCorrente>(),
        Conjunto(comValkey),
        scope.ServiceProvider.GetRequiredService<EscopoUsuario>(),
        NullLogger<Licencas>.Instance);

    private async Task<List<string>> MembrosAsync(Guid unidadeId) =>
        (await _multiplexer.GetDatabase().SortedSetRangeByRankAsync(ConjuntoLicencas.ChaveConjunto(_tenant.TenantId, unidadeId)))
            .Select(v => v.ToString())
            .ToList();

    private async Task<Guid> UsuarioAsync(params Guid[] unidades)
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

    private async Task<Guid> UsuarioEmpresaAsync(Guid principal)
    {
        var usuarioId = await UsuarioAsync(principal);
        using var scope = factory.Services.CreateScope();
        var db = _tenant.Contexto(scope, unidadeCorrente: null);
        var perfil = PerfilUsuario.Criar(usuarioId);
        perfil.DefinirEscopoEmpresa();
        db.PerfisUsuario.Add(perfil);
        await db.SaveChangesAsync();
        return usuarioId;
    }

    /// <summary>Login pelo caminho do comportamento: reserva, sessão nova, confirmação. Devolve a sessão e a unidade.</summary>
    private async Task<(Guid SessaoId, Guid UnidadeId)> EntrarAsync(Guid usuarioId, bool comValkey, Guid sessaoId)
    {
        using var scope = factory.Services.CreateScope();
        await using var reserva = await NovasLicencas(scope, comValkey).ReservarAsync(_tenant.TenantId, usuarioId, CancellationToken.None);
        reserva.ShouldNotBeNull();
        await reserva.ConfirmarAsync(sessaoId, [], CancellationToken.None);
        return (sessaoId, reserva.UnidadeId);
    }

    private Task<(Guid SessaoId, Guid UnidadeId)> EntrarAsync(Guid usuarioId, bool comValkey) => EntrarAsync(usuarioId, comValkey, Guid.NewGuid());

    private async Task<string> RecusaAsync(Guid usuarioId, bool comValkey) =>
        (await Should.ThrowAsync<ForbiddenException>(() => EntrarAsync(usuarioId, comValkey))).Message;

    private async Task<List<AtividadeSessao>> LinhasAsync(Guid usuarioId)
    {
        using var scope = factory.Services.CreateScope();
        return await _tenant.Contexto(scope, unidadeCorrente: null).AtividadesSessao.AsNoTracking()
            .Where(a => a.UsuarioId == usuarioId).ToListAsync();
    }

    private async Task<UnidadeTeste> CheiaAsync(bool comValkey)
    {
        var unidade = await _tenant.CriarUnidadeAsync(limiteSessoes: 1);
        await EntrarAsync(await UsuarioAsync(unidade.UnidadeId), comValkey);
        return unidade;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PrincipalCheia_SegundaComSaldo_AbreNaSegundaSemMarcaNaPrimeira(bool comValkey)
    {
        var a = await CheiaAsync(comValkey);
        var b = await _tenant.CriarUnidadeAsync(limiteSessoes: 1);
        var usuario = await UsuarioAsync(a.UnidadeId, b.UnidadeId);
        var membrosAAntes = comValkey ? await MembrosAsync(a.UnidadeId) : [];

        var (sessao, unidade) = await EntrarAsync(usuario, comValkey);

        unidade.ShouldBe(b.UnidadeId);
        (await LinhasAsync(usuario)).ShouldHaveSingleItem().UnidadeId.ShouldBe(b.UnidadeId);
        if (comValkey)
        {
            (await MembrosAsync(b.UnidadeId)).ShouldBe([ConjuntoLicencas.Membro(sessao)]);
            (await MembrosAsync(a.UnidadeId)).ShouldBe(membrosAAntes);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PrincipalInativa_AbreNaSegunda(bool comValkey)
    {
        var a = await _tenant.CriarUnidadeAsync(limiteSessoes: 5);
        var b = await _tenant.CriarUnidadeAsync(limiteSessoes: 5);
        await _tenant.ExecutarAsync($"""UPDATE paf."Unidades" SET "Ativa" = false WHERE "Id" = {a.UnidadeId}""");
        var usuario = await UsuarioAsync(a.UnidadeId, b.UnidadeId);

        (await EntrarAsync(usuario, comValkey)).UnidadeId.ShouldBe(b.UnidadeId);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SemContratoNaPrincipal_PulaParaASegunda(bool comValkey)
    {
        var a = await _tenant.CriarUnidadeAsync(limiteSessoes: null);
        var b = await _tenant.CriarUnidadeAsync(limiteSessoes: 1);
        var usuario = await UsuarioAsync(a.UnidadeId, b.UnidadeId);

        (await EntrarAsync(usuario, comValkey)).UnidadeId.ShouldBe(b.UnidadeId);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TodasCheias_RecusaComMensagemDeLimiteSemLinhaNemMembro(bool comValkey)
    {
        var a = await CheiaAsync(comValkey);
        var b = await CheiaAsync(comValkey);
        var usuario = await UsuarioAsync(a.UnidadeId, b.UnidadeId);
        var membrosAntes = comValkey ? (await MembrosAsync(a.UnidadeId)).Concat(await MembrosAsync(b.UnidadeId)).ToList() : [];

        (await RecusaAsync(usuario, comValkey)).ShouldBe(Licencas.MensagemLimite);

        (await LinhasAsync(usuario)).ShouldBeEmpty();
        if (comValkey)
        {
            (await MembrosAsync(a.UnidadeId)).Concat(await MembrosAsync(b.UnidadeId)).ShouldBe(membrosAntes);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task NenhumaAtiva_MensagemDeUnidadeInativa(bool comValkey)
    {
        var a = await _tenant.CriarUnidadeAsync(limiteSessoes: 5);
        var b = await _tenant.CriarUnidadeAsync(limiteSessoes: 5);
        await _tenant.ExecutarAsync($"""UPDATE paf."Unidades" SET "Ativa" = false WHERE "Id" IN ({a.UnidadeId}, {b.UnidadeId})""");

        (await RecusaAsync(await UsuarioAsync(a.UnidadeId, b.UnidadeId), comValkey)).ShouldBe(Licencas.MensagemSemUnidadeAtiva);
    }

    // The inactive unit's contract does not count: the only active one has none, so the cause is the contract.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AtivasSemContrato_MensagemDeContrato(bool comValkey)
    {
        var inativaComContrato = await _tenant.CriarUnidadeAsync(limiteSessoes: 5);
        var ativaSemContrato = await _tenant.CriarUnidadeAsync(limiteSessoes: null);
        await _tenant.ExecutarAsync($"""UPDATE paf."Unidades" SET "Ativa" = false WHERE "Id" = {inativaComContrato.UnidadeId}""");

        (await RecusaAsync(await UsuarioAsync(inativaComContrato.UnidadeId, ativaSemContrato.UnidadeId), comValkey))
            .ShouldBe(Licencas.MensagemSemContrato);
    }

    // RF22: a unit that allows overflow never refuses, so the cascade stops there even when it is full.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UltrapassarComAvisoCheia_NaoPula(bool comValkey)
    {
        var a = await CheiaAsync(comValkey);
        await _tenant.ExecutarAsync($"""
            UPDATE paf."ContratosUnidade" SET "Politica" = 'UltrapassarComAviso' WHERE "UnidadeId" = {a.UnidadeId}
            """);
        var b = await _tenant.CriarUnidadeAsync(limiteSessoes: 1);

        (await EntrarAsync(await UsuarioAsync(a.UnidadeId, b.UnidadeId), comValkey)).UnidadeId.ShouldBe(a.UnidadeId);
        if (comValkey)
        {
            (await MembrosAsync(a.UnidadeId)).Count.ShouldBe(2);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task EscopoEmpresa_PrincipalCheia_SegueParaUnidadeCriadaDepois(bool comValkey)
    {
        var principal = await CheiaAsync(comValkey);
        var usuario = await UsuarioEmpresaAsync(principal.UnidadeId);
        var criadaDepois = await _tenant.CriarUnidadeAsync(limiteSessoes: 1);

        // The tenant's initial unit (RF23) sits between them by creation and has no contract, so it is skipped too.
        (await EntrarAsync(usuario, comValkey)).UnidadeId.ShouldBe(criadaDepois.UnidadeId);
    }

    // Opposite scope orders racing: the degraded mode locks every candidate in id order, the set decides in one script.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task EscoposInvertidos_VinteLoginsParalelos_SemDeadlockEDentroDoLimite(bool comValkey)
    {
        var a = await _tenant.CriarUnidadeAsync(limiteSessoes: 5);
        var b = await _tenant.CriarUnidadeAsync(limiteSessoes: 5);
        var usuarios = new List<Guid>();
        for (var i = 0; i < 20; i++)
        {
            usuarios.Add(i % 2 == 0 ? await UsuarioAsync(a.UnidadeId, b.UnidadeId) : await UsuarioAsync(b.UnidadeId, a.UnidadeId));
        }

        var relogio = Stopwatch.StartNew();
        var resultados = await Task.WhenAll(usuarios.Select(u => Task.Run(async () =>
        {
            try
            {
                return (Guid?)(await EntrarAsync(u, comValkey)).UnidadeId;
            }
            catch (ForbiddenException erro) when (erro.Message == Licencas.MensagemLimite)
            {
                return null;
            }
        })));
        relogio.Stop();

        relogio.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(10));
        resultados.Count(r => r == a.UnidadeId).ShouldBe(5);
        resultados.Count(r => r == b.UnidadeId).ShouldBe(5);
        resultados.Count(r => r is null).ShouldBe(10);
        if (comValkey)
        {
            (await MembrosAsync(a.UnidadeId)).Count.ShouldBe(5);
            (await MembrosAsync(b.UnidadeId)).Count.ShouldBe(5);
        }
    }

    // Revival arrives from several windows at once: one row, one member, no leftover reservation.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CincoOcupacoesParalelasDaMesmaSessao_UmaLinhaEUmMembro(bool comValkey)
    {
        var a = await _tenant.CriarUnidadeAsync(limiteSessoes: 1);
        var b = await _tenant.CriarUnidadeAsync(limiteSessoes: 5);
        var usuario = await UsuarioAsync(a.UnidadeId, b.UnidadeId);
        var sessao = Guid.NewGuid();

        var unidades = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => Task.Run(async () => (await EntrarAsync(usuario, comValkey, sessao)).UnidadeId)));

        unidades.ShouldAllBe(u => u == a.UnidadeId);
        (await LinhasAsync(usuario)).ShouldHaveSingleItem().SessaoId.ShouldBe(sessao);
        if (comValkey)
        {
            (await MembrosAsync(a.UnidadeId)).ShouldBe([ConjuntoLicencas.Membro(sessao)]);
            (await MembrosAsync(b.UnidadeId)).ShouldBeEmpty();
        }
    }
}
