using System.Text.Json;
using FSH.Framework.Web.Idempotency;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Framework.Tests.Web;

/// <summary>
/// Runtime repro for audit findings API-01 (idempotency replay stores the wrong wire shape and
/// status) and CONC-01 (no in-flight reservation, so concurrent duplicate keys execute twice).
///
/// These exercise the REAL <see cref="IdempotencyEndpointFilter"/>. The one part we substitute is a
/// faithful single-store idempotency backend (write via HybridCache round-trips to the SAME
/// IDistributedCache the probe reads, using the filter's own serialization). This isolates the
/// filter's shape/status logic from the app's test-env cache split — the caveat that permanently
/// skips ChatSendMessageTests.SendMessage_Should_Replay_Same_Response_When_Idempotency_Key_Reused.
/// </summary>
public sealed class IdempotencyEndpointFilterReplayTests
{
    private const string Key = "fixed-idempotency-key";

    private static readonly JsonSerializerOptions CamelCase =
        new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    // ─── API-01: replayed status ─────────────────────────────────────

    [Fact]
    public async Task Replay_Should_PreserveCreatedStatus_When_FirstResponseWas201()
    {
        var provider = BuildProvider();
        var filter = new IdempotencyEndpointFilter();
        var id = Guid.NewGuid();

        // First call: handler returns a 201 Created (the framework would execute it AFTER the filter
        // returns — so at cache time Response.StatusCode is still the default 200).
        var first = NewContext(provider);
        await filter.InvokeAsync(
            new TestFilterContext(first),
            _ => ValueTask.FromResult<object?>(TypedResults.Created($"/samples/{id}", new SampleDto(id, "widget"))));

        // Second call, same key: must replay.
        var replayBody = new MemoryStream();
        var second = NewContext(provider, replayBody);
        await filter.InvokeAsync(
            new TestFilterContext(second),
            _ => throw new InvalidOperationException("handler must NOT run on an idempotent replay"));

        second.Response.Headers.ContainsKey("Idempotency-Replayed").ShouldBeTrue(
            "sanity: the replay path must actually engage, otherwise this test would be vacuous");
        second.Response.StatusCode.ShouldBe(
            StatusCodes.Status201Created,
            "a correct replay must reproduce the original 201 Created — the filter captures Response.StatusCode " +
            "BEFORE the IResult executes, so it caches (and replays) 200 instead.");
    }

    // ─── API-01: replayed body wire shape ────────────────────────────

    [Fact]
    public async Task Replay_Should_ReturnPlainDtoBody_Not_WrappedIResult()
    {
        var provider = BuildProvider();
        var filter = new IdempotencyEndpointFilter();
        var id = Guid.NewGuid();

        var first = NewContext(provider);
        await filter.InvokeAsync(
            new TestFilterContext(first),
            _ => ValueTask.FromResult<object?>(TypedResults.Ok(new SampleDto(id, "widget"))));

        var replayBody = new MemoryStream();
        var second = NewContext(provider, replayBody);
        await filter.InvokeAsync(
            new TestFilterContext(second),
            _ => throw new InvalidOperationException("handler must NOT run on an idempotent replay"));

        replayBody.Position = 0;
        using var doc = JsonDocument.Parse(replayBody.ToArray());

        doc.RootElement.TryGetProperty("value", out _).ShouldBeFalse(
            "a correct replay body is the wire DTO; the filter caches SerializeToUtf8Bytes(result) where " +
            "result is the wrapped Ok<T>/Created<T>, leaking the {\"value\":...} envelope onto the wire.");
        doc.RootElement.TryGetProperty("id", out _).ShouldBeTrue(
            "the plain DTO's own properties should be at the JSON root");
    }

    // ─── CONC-01: no in-flight reservation ───────────────────────────

    [Fact]
    public async Task Filter_Should_ExecuteHandlerOnce_When_TwoConcurrentRequestsShareKey()
    {
        var provider = BuildProvider();
        var filter = new IdempotencyEndpointFilter();

        int executions = 0;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // First request enters the handler and holds the in-flight reservation until released.
        EndpointFilterDelegate first = async _ =>
        {
            Interlocked.Increment(ref executions);
            started.SetResult();
            await release.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            return TypedResults.Ok(new SampleDto(Guid.NewGuid(), "first"));
        };

        // Second request shares the key; its handler must never run while the first is in flight.
        EndpointFilterDelegate second = _ =>
        {
            Interlocked.Increment(ref executions);
            return ValueTask.FromResult<object?>(TypedResults.Ok(new SampleDto(Guid.NewGuid(), "second")));
        };

        var firstCall = filter.InvokeAsync(new TestFilterContext(NewContext(provider)), first).AsTask();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5)); // first now holds the reservation

        var secondResult = await filter.InvokeAsync(new TestFilterContext(NewContext(provider)), second);

        release.SetResult();
        await firstCall.WaitAsync(TimeSpan.FromSeconds(10));

        executions.ShouldBe(
            1,
            "an idempotent endpoint must execute the handler exactly once for concurrent duplicate keys; " +
            "the second request should be rejected while the first is in flight.");
        (secondResult as IStatusCodeHttpResult)?.StatusCode.ShouldBe(
            StatusCodes.Status409Conflict,
            "a concurrent duplicate that arrives while the original is still running gets 409 Conflict.");
    }

    // ─── harness ─────────────────────────────────────────────────────

    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDistributedMemoryCache();
        services.AddSingleton<IOptions<IdempotencyOptions>>(Options.Create(new IdempotencyOptions()));
        services.AddSingleton<HybridCache>(sp =>
            new WriteThroughHybridCache(sp.GetRequiredService<IDistributedCache>(), CamelCase));
        return services.BuildServiceProvider();
    }

    private static DefaultHttpContext NewContext(IServiceProvider provider, Stream? responseBody = null)
    {
        var context = new DefaultHttpContext { RequestServices = provider };
        context.Request.Method = "POST";
        context.Request.Headers["Idempotency-Key"] = Key;
        if (responseBody is not null)
        {
            context.Response.Body = responseBody;
        }

        return context;
    }

    private sealed record SampleDto(Guid Id, string Name);

    private sealed class TestFilterContext : EndpointFilterInvocationContext
    {
        public TestFilterContext(HttpContext httpContext) => HttpContext = httpContext;

        public override HttpContext HttpContext { get; }

        public override IList<object?> Arguments { get; } = new List<object?>();

        public override T GetArgument<T>(int index) => (T)Arguments[index]!;
    }

    /// <summary>
    /// A faithful single-store idempotency backend: HybridCache.SetAsync serializes the cached
    /// response with the exact options the filter's probe uses and writes it into the same
    /// IDistributedCache, so a correctly-wired backend's replay is what surfaces the filter's bug.
    /// </summary>
    private sealed class WriteThroughHybridCache : HybridCache
    {
        private readonly IDistributedCache _store;
        private readonly JsonSerializerOptions _options;

        public WriteThroughHybridCache(IDistributedCache store, JsonSerializerOptions options)
        {
            _store = store;
            _options = options;
        }

        public override ValueTask<T> GetOrCreateAsync<TState, T>(
            string key,
            TState state,
            Func<TState, CancellationToken, ValueTask<T>> factory,
            HybridCacheEntryOptions? options = null,
            IEnumerable<string>? tags = null,
            CancellationToken cancellationToken = default) => factory(state, cancellationToken);

        public override async ValueTask SetAsync<T>(
            string key,
            T value,
            HybridCacheEntryOptions? options = null,
            IEnumerable<string>? tags = null,
            CancellationToken cancellationToken = default)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(value, _options);
            await _store.SetAsync(key, bytes, new DistributedCacheEntryOptions(), cancellationToken)
                .ConfigureAwait(false);
        }

        public override ValueTask RemoveAsync(string key, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public override ValueTask RemoveByTagAsync(string tag, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;
    }
}
