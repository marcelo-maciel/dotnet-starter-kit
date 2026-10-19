using System.Diagnostics;
using System.Net;
using FSH.Framework.Core.Exceptions;
using FSH.Modules.PAF.Contracts.v1.Sessao;
using FSH.Modules.PAF.Domain;
using FSH.Modules.PAF.Escopo;
using FSH.Modules.PAF.Features.v1.Sessao.SetUnidadeCorrente;
using FSH.Modules.PAF.Sessao;
using Integration.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;
using Testcontainers.Redis;

namespace Integration.Tests.Tests.PAF;

/// <summary>
/// Caminho com Valkey da unidade corrente, que o host do harness não exercita (ele sobe sem Valkey): estado da
/// sessão contra um contêiner Valkey real, e a troca de unidade com o tratador real sobre o PostgreSQL do harness.
/// </summary>
[Collection(FshCollectionDefinition.Name)]
public sealed class PafEstadoSessaoValkeyTests(FshWebApplicationFactory factory) : IAsyncLifetime
{
    private readonly RedisContainer _valkey = new RedisBuilder("valkey/valkey:9.1.0-alpine").Build();
    private readonly PafTenantCliente _tenant = new(factory, "paf-vk");
    private ConnectionMultiplexer _multiplexer = default!;

    public async Task InitializeAsync()
    {
        await Task.WhenAll(_valkey.StartAsync(), _tenant.InicializarAsync());
        _multiplexer = await ConnectionMultiplexer.ConnectAsync(_valkey.GetConnectionString());
    }

    public async Task DisposeAsync()
    {
        await _multiplexer.DisposeAsync();
        await _valkey.DisposeAsync();
    }

    private EstadoSessao Estado() => new([_multiplexer], NullLogger<EstadoSessao>.Instance);

    [Fact]
    public async Task Estado_DefineLeERemove_ComTenantNaChave()
    {
        var estado = Estado();
        var sessaoId = Guid.NewGuid();
        var unidadeId = Guid.NewGuid();

        (await estado.DefinirUnidadeAsync("tenant-a", sessaoId, unidadeId, CancellationToken.None)).ShouldBeTrue();

        (await estado.LerUnidadeAsync("tenant-a", sessaoId, CancellationToken.None)).ShouldBe(unidadeId);
        (await estado.LerUnidadeAsync("tenant-b", sessaoId, CancellationToken.None)).ShouldBeNull();
        var ttl = await _multiplexer.GetDatabase().KeyTimeToLiveAsync($"paf:tenant-a:sessao:{sessaoId}:unidade");
        ttl.ShouldNotBeNull();
        ttl.Value.ShouldBeLessThanOrEqualTo(TimeSpan.FromMinutes(Licencas.PrazoMarcaMinutos));
        (await estado.RemoverAsync("tenant-a", sessaoId, CancellationToken.None)).ShouldBeTrue();
        (await estado.LerUnidadeAsync("tenant-a", sessaoId, CancellationToken.None)).ShouldBeNull();
    }

    [Fact]
    public async Task Estado_SemValkeyRegistrado_RespondeComoAusente()
    {
        var estado = new EstadoSessao([], NullLogger<EstadoSessao>.Instance);

        (await estado.DefinirUnidadeAsync("t", Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None)).ShouldBeFalse();
        (await estado.LerUnidadeAsync("t", Guid.NewGuid(), CancellationToken.None)).ShouldBeNull();
    }

    [Fact]
    public async Task Estado_ValkeyInalcancavel_FalhaRapidoComoAusente()
    {
        // Nothing listens on this port; AbortOnConnectFail=false is how the kit connects (Caching/Extensions.cs).
        var opcoes = ConfigurationOptions.Parse("127.0.0.1:1");
        opcoes.AbortOnConnectFail = false;
        opcoes.ConnectTimeout = 200;
        await using var morto = await ConnectionMultiplexer.ConnectAsync(opcoes);
        var estado = new EstadoSessao([morto], NullLogger<EstadoSessao>.Instance);
        var relogio = Stopwatch.StartNew();

        (await estado.DefinirUnidadeAsync("t", Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None)).ShouldBeFalse();
        (await estado.LerUnidadeAsync("t", Guid.NewGuid(), CancellationToken.None)).ShouldBeNull();

        relogio.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task TrocaDeUnidade_ComValkey_GravaEstadoESoltaOGuiche()
    {
        var (origem, destino, sessaoId, usuarioId) = await PrepararAsync();
        await VincularAsync(origem.GuicheA, sessaoId);

        using var scope = factory.Services.CreateScope();
        var handler = Handler(scope, origem, sessaoId, usuarioId, out var estado);
        await handler.Handle(new SetUnidadeCorrenteCommand(destino.UnidadeId), CancellationToken.None);

        (await estado.LerUnidadeAsync(_tenant.TenantId, sessaoId, CancellationToken.None)).ShouldBe(destino.UnidadeId);
        (await VinculosAtivosAsync(sessaoId)).ShouldBe(0);
    }

    [Fact]
    public async Task TrocaDeUnidade_ComAtendimentoEmCurso_RecusadaSemTocarNoValkey()
    {
        var (origem, destino, sessaoId, usuarioId) = await PrepararAsync();
        await VincularAsync(origem.GuicheA, sessaoId);
        await _tenant.ChamarNoGuicheAsync(origem);

        using var scope = factory.Services.CreateScope();
        var handler = Handler(scope, origem, sessaoId, usuarioId, out var estado);
        var erro = await Should.ThrowAsync<CustomException>(
            () => handler.Handle(new SetUnidadeCorrenteCommand(destino.UnidadeId), CancellationToken.None).AsTask());

        erro.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await estado.LerUnidadeAsync(_tenant.TenantId, sessaoId, CancellationToken.None)).ShouldBeNull();
        (await VinculosAtivosAsync(sessaoId)).ShouldBe(1);
    }

    private SetUnidadeCorrenteCommandHandler Handler(
        IServiceScope scope, UnidadeTeste origem, Guid sessaoId, Guid usuarioId, out EstadoSessao estado)
    {
        var db = _tenant.Contexto(scope, unidadeCorrente: origem.UnidadeId);
        var sessao = new SessaoCorrente();
        sessao.Definir(_tenant.TenantId, sessaoId, usuarioId, origem.UnidadeId);
        estado = Estado();
        return new SetUnidadeCorrenteCommandHandler(db, sessao, new ConcessaoCorrente(), scope.ServiceProvider.GetRequiredService<IUnidadeCorrente>(), estado,
            scope.ServiceProvider.GetRequiredService<EscopoUsuario>());
    }

    private async Task<(UnidadeTeste Origem, UnidadeTeste Destino, Guid SessaoId, Guid UsuarioId)> PrepararAsync()
    {
        var origem = await _tenant.CriarUnidadeAsync(limiteSessoes: 1);
        var destino = await _tenant.CriarUnidadeAsync(limiteSessoes: 1);
        var usuario = await _tenant.CriarUsuarioAsync(origem.UnidadeId, destino.UnidadeId);
        return (origem, destino, Guid.NewGuid(), Guid.Parse(usuario.UserId));
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
}
