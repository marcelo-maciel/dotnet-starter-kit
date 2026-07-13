using System.Text.Json;
using FSH.Framework.Web.Idempotency;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;
using StackExchange.Redis;

namespace Framework.Tests.Web;

/// <summary>
/// Regression for audit findings API-01 (idempotency replay stored the wrong wire shape and status)
/// and CONC-01 (no in-flight reservation, so concurrent duplicate keys executed twice). These
/// exercise the REAL <see cref="IdempotencyEndpointFilter"/> against a real in-memory
/// <see cref="IDistributedCache"/> — the same store the filter now uses for both probe and write.
/// </summary>
public sealed class IdempotencyEndpointFilterReplayTests
{
    private const string Key = "fixed-idempotency-key";

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

    // ─── HIGH: reservation TTL is the short ReservationTtl, not the 24h response TTL ─────

    [Fact]
    public async Task Reservation_Should_UseReservationTtl_Not_ResponseTtl()
    {
        var options = new IdempotencyOptions
        {
            ReservationTtl = TimeSpan.FromSeconds(37), // distinct from DefaultTtl to prove which one is used
        };
        var db = Substitute.For<IDatabase>();
        TimeSpan? capturedTtl = null;
        db.StringSetAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<TimeSpan?>(), Arg.Any<When>())
            .Returns(ci => { capturedTtl = ci.ArgAt<TimeSpan?>(2); return Task.FromResult(true); });
        var provider = BuildProvider(options, RedisMultiplexer(db));
        var filter = new IdempotencyEndpointFilter();

        await filter.InvokeAsync(
            new TestFilterContext(NewContext(provider)),
            _ => ValueTask.FromResult<object?>(TypedResults.Ok(new SampleDto(Guid.NewGuid(), "widget"))));

        capturedTtl.ShouldBe(
            options.ReservationTtl,
            "the in-flight reservation must use the short ReservationTtl; keying it to the 24h response TTL " +
            "would strand the lock for a day if the process is killed before the finally-release runs.");
        capturedTtl.ShouldNotBe(options.DefaultTtl);
    }

    // ─── MEDIUM + nit: a Redis fault on reserve/release fails open, never 500s ───────────

    [Fact]
    public async Task Filter_Should_ProceedWithoutThrowing_When_RedisReservationFaults()
    {
        var db = Substitute.For<IDatabase>();
        db.StringSetAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<TimeSpan?>(), Arg.Any<When>())
            .Returns(Task.FromException<bool>(new RedisException("reserve blip")));
        db.KeyDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns(Task.FromException<bool>(new RedisException("release blip")));
        var provider = BuildProvider(new IdempotencyOptions(), RedisMultiplexer(db));
        var filter = new IdempotencyEndpointFilter();

        int executions = 0;
        var result = await filter.InvokeAsync(
            new TestFilterContext(NewContext(provider)),
            _ => { executions++; return ValueTask.FromResult<object?>(TypedResults.Ok(new SampleDto(Guid.NewGuid(), "widget"))); });

        executions.ShouldBe(
            1,
            "a transient Redis error on the reservation must fail open — the handler still runs. On main " +
            "idempotency degraded gracefully; treating the reservation as authoritative would 500 the request.");
        result.ShouldNotBeNull("the request must complete normally, not throw out of the filter");
    }

    // ─── harness ─────────────────────────────────────────────────────

    private static ServiceProvider BuildProvider() => BuildProvider(new IdempotencyOptions(), multiplexer: null);

    private static ServiceProvider BuildProvider(IdempotencyOptions options, IConnectionMultiplexer? multiplexer)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDistributedMemoryCache();
        services.AddSingleton<IOptions<IdempotencyOptions>>(Options.Create(options));
        if (multiplexer is not null)
        {
            services.AddSingleton(multiplexer);
        }

        return services.BuildServiceProvider();
    }

    private static IConnectionMultiplexer RedisMultiplexer(IDatabase db)
    {
        var mux = Substitute.For<IConnectionMultiplexer>();
        mux.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(db);
        return mux;
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
}
