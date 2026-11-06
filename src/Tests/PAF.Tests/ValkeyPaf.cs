using StackExchange.Redis;
using Testcontainers.Redis;

namespace PAF.Tests;

/// <summary>
/// Valkey real por Testcontainers para os testes do PAF que dependem dele (conjunto de licenças, registro de
/// conexões). Um contêiner por coleção; os testes isolam-se por tenant aleatório na chave. Pausável, para o caminho
/// de Valkey que não responde dentro do prazo, e esvaziável, para o de Valkey que volta sem dados.
/// </summary>
public sealed class ValkeyPaf : IAsyncLifetime
{
    // Same image as the Integration.Tests Valkey and the compose of the environments.
    private readonly RedisContainer _container = new RedisBuilder("valkey/valkey:9.1.0-alpine").Build();

    public IConnectionMultiplexer Multiplexer { get; private set; } = default!;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        Multiplexer = await NovoMultiplexerAsync();
    }

    public async Task DisposeAsync()
    {
        await Multiplexer.DisposeAsync();
        await _container.DisposeAsync();
    }

    /// <summary>
    /// Conexão própria, para o teste que pausa o contêiner não deixar a compartilhada com comandos pendentes.
    /// <c>AllowAdmin</c> libera o FLUSHALL; os prazos são os curtos que o ambiente usa.
    /// </summary>
    public async Task<IConnectionMultiplexer> NovoMultiplexerAsync()
    {
        var opcoes = ConfigurationOptions.Parse(_container.GetConnectionString());
        opcoes.AllowAdmin = true;
        opcoes.AbortOnConnectFail = false;
        opcoes.ConnectTimeout = 2000;
        opcoes.SyncTimeout = 2000;
        opcoes.AsyncTimeout = 2000;
        return await ConnectionMultiplexer.ConnectAsync(opcoes);
    }

    public Task EsvaziarAsync() => Multiplexer.GetServers()[0].FlushAllDatabasesAsync();

    public Task PausarAsync(CancellationToken ct) => _container.PauseAsync(ct);

    public Task RetomarAsync(CancellationToken ct) => _container.UnpauseAsync(ct);
}

[CollectionDefinition(Nome)]
public sealed class ValkeyPafColecao : ICollectionFixture<ValkeyPaf>
{
    public const string Nome = "ValkeyPaf";
}
