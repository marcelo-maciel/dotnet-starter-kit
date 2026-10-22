using FSH.Framework.Core.Exceptions;
using FSH.Modules.PAF.Contracts.v1.Sessao;
using FSH.Modules.PAF.Domain;
using FSH.Modules.PAF.Escopo;
using FSH.Modules.PAF.Sessao;
using Integration.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;
using Testcontainers.Redis;

namespace Integration.Tests.Tests.PAF;

/// <summary>
/// Excedente de licença (S08, RF22, ARQ seção 9, D03): a ocupação acima do limite numa unidade com
/// <c>UltrapassarComAviso</c> grava uma linha em <c>ConsumosLicenca</c> na mesma transação, com o conjunto no Valkey e no
/// modo degradado; a ocupação que falha não deixa linha nem vaga; <c>Recusar</c> nunca grava. A tela lê o aviso por
/// <c>GET paf/sessao/aviso-licenca</c>. <see cref="Licencas"/> é montada à mão, como em
/// <see cref="PafCascataLicencaTests"/>, porque o host do harness sobe sem Valkey.
/// </summary>
[Collection(FshCollectionDefinition.Name)]
public sealed class PafConsumoLicencaTests(FshWebApplicationFactory factory) : IAsyncLifetime
{
    private static readonly TimeSpan Prazo = TimeSpan.FromMinutes(Licencas.PrazoMarcaMinutos);
    private readonly RedisContainer _valkey = new RedisBuilder("valkey/valkey:9.1.0-alpine").Build();
    private readonly PafTenantCliente _tenant = new(factory, "paf-cl");
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

    private async Task<Guid> UsuarioAsync(Guid unidadeId)
    {
        var usuarioId = Guid.NewGuid();
        using var scope = factory.Services.CreateScope();
        var db = _tenant.Contexto(scope, unidadeCorrente: null);
        db.UsuariosUnidade.Add(UsuarioUnidade.Criar(usuarioId, unidadeId, 0));
        await db.SaveChangesAsync();
        return usuarioId;
    }

    private async Task<Guid> EntrarAsync(Guid usuarioId, bool comValkey)
    {
        var sessaoId = Guid.NewGuid();
        using var scope = factory.Services.CreateScope();
        await using var reserva = await NovasLicencas(scope, comValkey).ReservarAsync(_tenant.TenantId, usuarioId, CancellationToken.None);
        reserva.ShouldNotBeNull();
        await reserva.ConfirmarAsync(sessaoId, [], CancellationToken.None);
        return sessaoId;
    }

    private async Task<UnidadeTeste> UnidadeAsync(int limite, PoliticaLimiteLicenca politica)
    {
        var unidade = await _tenant.CriarUnidadeAsync(limiteSessoes: limite);
        await _tenant.ExecutarAsync($"""
            UPDATE paf."ContratosUnidade" SET "Politica" = {politica.ToString()}
             WHERE "UnidadeId" = {unidade.UnidadeId} AND "TenantId" = {_tenant.TenantId}
            """);
        return unidade;
    }

    // Only the excess rows: the host's Hangfire server runs ManutencaoLicencas on every 5-minute boundary (UTC) and
    // writes an Amostra row for each unit with a contract, this test's unit included, whenever a test spans the tick.
    private async Task<List<ConsumoLicenca>> ExcedentesAsync(Guid unidadeId)
    {
        using var scope = factory.Services.CreateScope();
        return await _tenant.Contexto(scope, unidadeCorrente: unidadeId).ConsumosLicenca.AsNoTracking()
            .Where(c => c.UnidadeId == unidadeId && c.Tipo == TipoConsumoLicenca.Excedente).ToListAsync();
    }

    private async Task<List<string>> MembrosAsync(Guid unidadeId) =>
        (await _multiplexer.GetDatabase().SortedSetRangeByRankAsync(ConjuntoLicencas.ChaveConjunto(_tenant.TenantId, unidadeId)))
            .Select(v => v.ToString())
            .ToList();

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UltrapassarComAviso_AcimaDoLimite_GravaUmExcedenteComLimiteMaisUm(bool comValkey)
    {
        var unidade = await UnidadeAsync(limite: 1, PoliticaLimiteLicenca.UltrapassarComAviso);
        await EntrarAsync(await UsuarioAsync(unidade.UnidadeId), comValkey);
        (await ExcedentesAsync(unidade.UnidadeId)).ShouldBeEmpty("dentro do limite não há excedente");

        var acima = await EntrarAsync(await UsuarioAsync(unidade.UnidadeId), comValkey);

        var excedente = (await ExcedentesAsync(unidade.UnidadeId)).ShouldHaveSingleItem();
        excedente.Tipo.ShouldBe(TipoConsumoLicenca.Excedente);
        excedente.Ocupadas.ShouldBe(2);
        excedente.Limite.ShouldBe(1);
        excedente.SessaoId.ShouldBe(acima);
        excedente.RegistradoEm.ShouldBeGreaterThan(DateTimeOffset.UtcNow.AddMinutes(-1));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Recusar_UnidadeCheia_RecusaENuncaGrava(bool comValkey)
    {
        var unidade = await UnidadeAsync(limite: 1, PoliticaLimiteLicenca.Recusar);
        await EntrarAsync(await UsuarioAsync(unidade.UnidadeId), comValkey);

        var recusado = await UsuarioAsync(unidade.UnidadeId);

        await Should.ThrowAsync<ForbiddenException>(() => EntrarAsync(recusado, comValkey));

        (await ExcedentesAsync(unidade.UnidadeId)).ShouldBeEmpty();
    }

    /// <summary>
    /// A falha forçada vem do próprio PostgreSQL: outra transação segura, sem commit, a linha de AtividadesSessao da
    /// mesma sessão, e a confirmação, que já gravou o excedente, espera nela até o prazo cancelar. Tudo volta.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FalhaDepoisDoExcedente_NaoDeixaLinhaNemOcupacao(bool comValkey)
    {
        var unidade = await UnidadeAsync(limite: 1, PoliticaLimiteLicenca.UltrapassarComAviso);
        await EntrarAsync(await UsuarioAsync(unidade.UnidadeId), comValkey);
        var membrosAntes = comValkey ? await MembrosAsync(unidade.UnidadeId) : [];
        var usuario = await UsuarioAsync(unidade.UnidadeId);
        var sessaoId = Guid.NewGuid();

        using var escopoBloqueio = factory.Services.CreateScope();
        var bloqueio = _tenant.Contexto(escopoBloqueio, unidadeCorrente: null);
        await using (var transacaoBloqueio = await bloqueio.Database.BeginTransactionAsync())
        {
            await bloqueio.Database.ExecuteSqlAsync($"""
                INSERT INTO paf."AtividadesSessao" ("SessaoId", "UsuarioId", "UnidadeId", "UltimaAtividadeEm", "TenantId")
                VALUES ({sessaoId}, {Guid.NewGuid()}, {unidade.UnidadeId}, now(), {_tenant.TenantId})
                """);

            using var scope = factory.Services.CreateScope();
            await using (var reserva = await NovasLicencas(scope, comValkey).ReservarAsync(_tenant.TenantId, usuario, CancellationToken.None))
            {
                reserva.ShouldNotBeNull();
                using var prazo = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                Exception? falha = null;
                try
                {
                    await reserva.ConfirmarAsync(sessaoId, [], prazo.Token);
                }
                catch (OperationCanceledException ex)
                {
                    falha = ex;
                }

                falha.ShouldNotBeNull("a confirmação devia esperar na linha travada até o prazo cancelar");
            }

            await transacaoBloqueio.RollbackAsync();
        }

        (await ExcedentesAsync(unidade.UnidadeId)).ShouldBeEmpty("o excedente vai junto com a ocupação que falhou");
        (await _tenant.AtividadesAsync(usuario)).ShouldBeEmpty();
        if (comValkey)
        {
            (await MembrosAsync(unidade.UnidadeId)).ShouldBe(membrosAntes, "a vaga tomada no conjunto volta");
        }
    }

    /// <summary>D03 pelo caminho real: login HTTP (o harness não tem Valkey, então o modo degradado decide).</summary>
    [Fact]
    public async Task AvisoLicenca_SoParaQuemEntrouAcimaDoLimite()
    {
        var unidade = await UnidadeAsync(limite: 1, PoliticaLimiteLicenca.UltrapassarComAviso);
        var dentro = await _tenant.EntrarAsync(await _tenant.CriarUsuarioAsync(unidade.UnidadeId));
        var acima = await _tenant.EntrarAsync(await _tenant.CriarUsuarioAsync(unidade.UnidadeId));

        using var clienteDentro = _tenant.Cliente(dentro.AccessToken);
        using var semAviso = await clienteDentro.GetAsync("/api/v1/paf/sessao/aviso-licenca");
        semAviso.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        using var clienteAcima = _tenant.Cliente(acima.AccessToken);
        using var comAviso = await clienteAcima.GetAsync("/api/v1/paf/sessao/aviso-licenca");
        comAviso.StatusCode.ShouldBe(HttpStatusCode.OK, await comAviso.Content.ReadAsStringAsync());
        var aviso = (await comAviso.Content.ReadFromJsonAsync<AvisoLicencaDto>()).ShouldNotBeNull();
        aviso.UnidadeId.ShouldBe(unidade.UnidadeId);
        aviso.Ocupadas.ShouldBe(2);
        aviso.Limite.ShouldBe(1);
        aviso.Unidade.ShouldStartWith("Loja ");
        var excedente = (await ExcedentesAsync(unidade.UnidadeId)).ShouldHaveSingleItem();
        aviso.RegistroId.ShouldBe(excedente.Id);
        excedente.SessaoId.ShouldBe(PafTenantCliente.SessaoDoToken(acima.AccessToken));
    }
}
