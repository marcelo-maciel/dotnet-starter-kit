using System.Collections.Concurrent;
using System.Diagnostics;
using Finbuckle.MultiTenant;
using Finbuckle.MultiTenant.Abstractions;
using FSH.Framework.Persistence;
using FSH.Framework.Shared.Multitenancy;
using FSH.Modules.Multitenancy.Data;
using FSH.Modules.PAF.Contracts.v1.TempoReal;
using FSH.Modules.PAF.Sessao;
using Microsoft.AspNetCore.SignalR.Client;
using PAF.Kestrel.Tests;
using Testcontainers.PostgreSql;
using Testcontainers.Redis;

namespace PAF.Latencia.Tests;

/// <summary>
/// The measured system: PostgreSQL and Valkey in containers, the Valkey reached only through
/// <see cref="RetransmissorComAtraso"/>, instance A (where the guichês call, the totens and the painéis of the
/// single-instance measurement connect) and instance B (where a second connection of each painel sits, for the
/// reference hop through the backplane), and the D20 volume connected over real WebSockets.
/// </summary>
public sealed class AmbienteLatencia : IAsyncLifetime, IAsyncDisposable
{
    /// <summary>Fichas emitted and aged per unidade before any call (D20: 600 a day per unidade; enough for every phase).</summary>
    public const int FichasPorUnidade = 30;

    // Same ceilings the environments set (CONTEXT.md, S14).
    private const string PrazosValkey = "connectTimeout=2000,syncTimeout=500,asyncTimeout=500,abortConnect=false";

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine")
        .WithDatabase("fsh_paf_latencia")
        .WithUsername("postgres")
        .WithPassword("integration_test_pwd")
        .WithAutoRemove(true)
        .WithCleanUp(true)
        .Build();

    private readonly RedisContainer _valkey = new RedisBuilder("valkey/valkey:9.1.0-alpine").Build();
    private readonly List<ConexaoWebSocket> _conexoes = [];
    private readonly ConcurrentDictionary<(Guid Conexao, Guid Atendimento), TaskCompletionSource<long>> _chegadas = new();
    private RetransmissorComAtraso _retransmissor = default!;
    private int _liberado;

    internal HostLatencia InstanciaA { get; private set; } = default!;

    internal HostLatencia InstanciaB { get; private set; } = default!;

    internal CargaReferencia Carga { get; private set; } = default!;

    /// <summary>Connection id of each painel connected to instance A, by unidade.</summary>
    internal ILookup<Guid, Guid> PaineisA { get; private set; } = default!;

    /// <summary>Connection id of each painel connected to instance B, by unidade.</summary>
    internal ILookup<Guid, Guid> PaineisB { get; private set; } = default!;

    internal RetransmissorComAtraso Retransmissor => _retransmissor;

    public async Task InitializeAsync()
    {
        await Task.WhenAll(_postgres.StartAsync(), _valkey.StartAsync());
        _retransmissor = new RetransmissorComAtraso(_valkey.Hostname, _valkey.GetMappedPublicPort(6379));
        _retransmissor.Iniciar();
        var valkey = $"127.0.0.1:{_retransmissor.Porta},{PrazosValkey}";
        var postgres = $"{_postgres.GetConnectionString()};Timeout=5;Command Timeout=30";

        InstanciaA = new HostLatencia(postgres, valkey);
        InstanciaA.Iniciar();
        await ProvisionarRaizAsync(InstanciaA);
        InstanciaB = new HostLatencia(postgres, valkey);
        InstanciaB.Iniciar();

        Carga = new CargaReferencia(InstanciaA);
        await Carga.PrepararAsync(FichasPorUnidade);

        var paineisA = new List<(Guid Unidade, Guid Conexao)>();
        var paineisB = new List<(Guid Unidade, Guid Conexao)>();
        foreach (var painel in Carga.Paineis)
        {
            paineisA.Add((painel.UnidadeId, await ConectarPainelAsync(InstanciaA, painel.Token)));
            paineisB.Add((painel.UnidadeId, await ConectarPainelAsync(InstanciaB, painel.Token)));
        }

        PaineisA = paineisA.ToLookup(p => p.Unidade, p => p.Conexao);
        PaineisB = paineisB.ToLookup(p => p.Unidade, p => p.Conexao);

        // The rest of the reference volume on line, as the front keeps it: every totem and every guichê screen.
        foreach (var token in Carga.Totens.Select(t => t.Token).Concat(Carga.Atendentes.Select(a => a.Token)))
        {
            await ConectarAsync(InstanciaA, token);
        }
    }

    Task IAsyncLifetime.DisposeAsync() => LiberarAsync();

    async ValueTask IAsyncDisposable.DisposeAsync() => await LiberarAsync();

    // xunit may reach the fixture through both interfaces: the second call finds everything released.
    private async Task LiberarAsync()
    {
        if (Interlocked.Exchange(ref _liberado, 1) == 1)
        {
            return;
        }

        foreach (var conexao in _conexoes)
        {
            await conexao.DisposeAsync();
        }

        foreach (var atendente in Carga?.Atendentes ?? [])
        {
            atendente.Cliente.Dispose();
        }

        Carga?.Admin?.Dispose();
        if (InstanciaB is not null)
        {
            await InstanciaB.DisposeAsync();
        }

        if (InstanciaA is not null)
        {
            await InstanciaA.DisposeAsync();
        }

        if (_retransmissor is not null)
        {
            await _retransmissor.DisposeAsync();
        }

        await _postgres.DisposeAsync();
        await _valkey.DisposeAsync();
    }

    /// <summary>Instant (Stopwatch timestamp) the painel connection received the SenhaChamada of the atendimento.</summary>
    internal Task<long> Chegada(Guid conexao, Guid atendimento) =>
        _chegadas.GetOrAdd((conexao, atendimento), _ => new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously)).Task;

    private async Task<Guid> ConectarPainelAsync(HostLatencia instancia, string token)
    {
        var id = Guid.NewGuid();
        var conexao = new ConexaoWebSocket(instancia.Endereco, Carga.TenantId, () => Task.FromResult(token));
        // The clock is read first thing in the handler: the same process clock stamped the request (single clock).
        conexao.Hub.On<SenhaChamada>(PafHubMetodos.SenhaChamada, aviso =>
        {
            var instante = Stopwatch.GetTimestamp();
            _chegadas.GetOrAdd((id, aviso.AtendimentoId), _ => new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously))
                .TrySetResult(instante);
        });
        _conexoes.Add(conexao);
        await conexao.Hub.StartAsync();
        await conexao.ProntaAsync();
        conexao.Hub.State.ShouldBe(HubConnectionState.Connected);
        conexao.Tentativas.Select(t => t.Status).ShouldBe([HttpStatusCode.SwitchingProtocols]);
        return id;
    }

    private async Task ConectarAsync(HostLatencia instancia, string token)
    {
        var conexao = new ConexaoWebSocket(instancia.Endereco, Carga.TenantId, () => Task.FromResult(token));
        _conexoes.Add(conexao);
        await conexao.Hub.StartAsync();
        await conexao.ProntaAsync();
        conexao.Hub.State.ShouldBe(HubConnectionState.Connected);
    }

    // Root tenant, every module migration and seed, as the other harnesses; then the licence set, which the hosted
    // rebuild tried before the migrations existed.
    private static async Task ProvisionarRaizAsync(HostLatencia host)
    {
        using var prazo = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        using (var scope = host.Services.CreateScope())
        {
            var tenants = scope.ServiceProvider.GetRequiredService<TenantDbContext>();
            await tenants.Database.MigrateAsync(prazo.Token);
            var raiz = await tenants.TenantInfo.FindAsync([MultitenancyConstants.Root.Id], prazo.Token);
            if (raiz is null)
            {
                raiz = new AppTenantInfo(
                    MultitenancyConstants.Root.Id,
                    MultitenancyConstants.Root.Name,
                    string.Empty,
                    MultitenancyConstants.Root.EmailAddress,
                    issuer: MultitenancyConstants.Root.Issuer);
                raiz.SetValidity(DateTime.UtcNow.AddYears(1));
                await tenants.TenantInfo.AddAsync(raiz, prazo.Token);
                await tenants.SaveChangesAsync(prazo.Token);
            }

            scope.ServiceProvider.GetRequiredService<IMultiTenantContextSetter>().MultiTenantContext =
                new MultiTenantContext<AppTenantInfo>(raiz);
            foreach (var init in scope.ServiceProvider.GetServices<IDbInitializer>())
            {
                await init.MigrateAsync(prazo.Token);
            }

            foreach (var init in scope.ServiceProvider.GetServices<IDbInitializer>())
            {
                await init.SeedAsync(prazo.Token);
            }

            await scope.ServiceProvider.GetRequiredService<FSH.Modules.Identity.Authorization.RolePermissionSyncer>().SyncAsync(prazo.Token);
        }

        (await host.Services.GetRequiredService<ReconstrucaoLicencas>().ReconstruirAsync(prazo.Token)).ShouldBeTrue();
    }
}

/// <summary>Runs alone: two hosts reset the kit's static ModuleLoader, and the measurement must not share the CPU.</summary>
[CollectionDefinition(Nome, DisableParallelization = true)]
public sealed class AmbienteLatenciaColecao : ICollectionFixture<AmbienteLatencia>
{
    public const string Nome = "PafLatencia";
}
