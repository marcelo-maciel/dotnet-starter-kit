using System.Text.Json;
using FSH.Framework.Caching;
using FSH.Framework.Web.Idempotency;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
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

    // Mirrors the serializer the filter stores entries with.
    private static readonly JsonSerializerOptions CacheJsonOpts = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

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

    // ─── MEDIUM: replay must carry the headers the IResult set (Location on 201) ─────────

    [Fact]
    public async Task Replay_Should_PreserveLocationHeader_When_FirstResponseWasCreated()
    {
        var provider = BuildProvider();
        var filter = new IdempotencyEndpointFilter();
        var id = Guid.NewGuid();
        var location = $"/samples/{id}";

        var first = NewContext(provider, new MemoryStream());
        await filter.InvokeAsync(
            new TestFilterContext(first),
            _ => ValueTask.FromResult<object?>(TypedResults.Created(location, new SampleDto(id, "widget"))));

        first.Response.Headers.Location.ToString().ShouldBe(
            location,
            "sanity: executing Created(uri, value) is what sets Location, so the first call must have it");

        var second = NewContext(provider, new MemoryStream());
        await filter.InvokeAsync(
            new TestFilterContext(second),
            _ => throw new InvalidOperationException("handler must NOT run on an idempotent replay"));

        second.Response.Headers.Location.ToString().ShouldBe(
            location,
            "a replayed 201 without Location breaks any client that follows the header — and only under " +
            "the retry conditions nobody tests. The captured response must carry the meaningful headers.");
    }

    // ─── HIGH: the stored response must outlive the request that produced it ─────────────

    [Fact]
    public async Task FirstCall_Should_StillCacheResponse_When_ClientDisconnectsAfterHandlerRan()
    {
        var provider = BuildProvider();
        var filter = new IdempotencyEndpointFilter();
        var id = Guid.NewGuid();

        using var aborted = new CancellationTokenSource();
        await aborted.CancelAsync();
        var first = NewContext(provider, new MemoryStream());
        first.RequestAborted = aborted.Token;

        try
        {
            await filter.InvokeAsync(
                new TestFilterContext(first),
                _ => ValueTask.FromResult<object?>(TypedResults.Ok(new SampleDto(id, "widget"))));
        }
        catch (OperationCanceledException)
        {
            // Writing to a socket the client already closed is allowed to fail — the handler's side
            // effect has committed by then, so the stored response must survive it regardless.
        }

        var replayBody = new MemoryStream();
        var second = NewContext(provider, replayBody);
        await filter.InvokeAsync(
            new TestFilterContext(second),
            _ => throw new InvalidOperationException(
                "handler must NOT re-run: client-timeout-then-retry is the exact duplicate this feature defends against"));

        second.Response.Headers.ContainsKey("Idempotency-Replayed").ShouldBeTrue(
            "the store must not be tied to the client's connection: if it is, a client that times out and " +
            "retries re-executes the handler — the single most common way a duplicate is generated.");

        using var doc = JsonDocument.Parse(replayBody.ToArray());
        doc.RootElement.GetProperty("id").GetGuid().ShouldBe(
            id,
            "capturing under RequestAborted is worse than not caching: WriteAsJsonAsync swallows the " +
            "cancellation, so an EMPTY body gets stored and replayed as a 200 for the full TTL.");
    }

    // ─── a bodiless success must not gain a content type it never had ───────────────────

    [Fact]
    public async Task Replay_Should_NotInventContentType_When_FirstResponseWasNoContent()
    {
        var provider = BuildProvider();
        var filter = new IdempotencyEndpointFilter();

        var first = NewContext(provider, new MemoryStream());
        await filter.InvokeAsync(
            new TestFilterContext(first),
            _ => ValueTask.FromResult<object?>(TypedResults.NoContent()));

        first.Response.ContentType.ShouldBeNull("sanity: a 204 carries no content type");

        var second = NewContext(provider, new MemoryStream());
        await filter.InvokeAsync(
            new TestFilterContext(second),
            _ => throw new InvalidOperationException("handler must NOT run on an idempotent replay"));

        second.Response.StatusCode.ShouldBe(StatusCodes.Status204NoContent);
        second.Response.ContentType.ShouldBeNull(
            "defaulting the captured content type to application/json replays a 204 that advertises a JSON " +
            "body it does not have.");
    }

    // ─── note: a failure response must not lock the key out for the full 24h TTL ─────────

    [Fact]
    public async Task Filter_Should_NotCacheResponse_When_FirstResponseIsNotSuccessful()
    {
        var provider = BuildProvider();
        var filter = new IdempotencyEndpointFilter();

        var first = NewContext(provider, new MemoryStream());
        await filter.InvokeAsync(
            new TestFilterContext(first),
            _ => ValueTask.FromResult<object?>(TypedResults.Conflict("downstream busy")));

        int executions = 0;
        var second = NewContext(provider, new MemoryStream());
        await filter.InvokeAsync(
            new TestFilterContext(second),
            _ =>
            {
                executions++;
                return ValueTask.FromResult<object?>(TypedResults.Ok(new SampleDto(Guid.NewGuid(), "widget")));
            });

        executions.ShouldBe(
            1,
            "caching a non-2xx locks the caller out of retrying that key for the full response TTL (24h) after " +
            "a transient downstream failure. Only a successful response is a record of a committed side effect.");
    }

    // ─── an unreadable entry is a miss, not a 500 ───────────────────────────────────────

    [Fact]
    public async Task Filter_Should_RunHandler_When_CachedEntryIsUnreadable()
    {
        var provider = BuildProvider();
        var filter = new IdempotencyEndpointFilter();
        var cache = provider.GetRequiredService<IDistributedCache>();

        // The key the filter reads: tenant + operation + caller key. Proven below by seeding a VALID
        // entry at it first — otherwise a wrong key here would make the real assertion pass as a
        // plain cache miss and the test would assert nothing.
        var storedKey = CacheKeys.IdempotencyEntry("global", $"POST::{Key}");
        await cache.SetAsync(
            storedKey,
            JsonSerializer.SerializeToUtf8Bytes(
                new CachedIdempotentResponse { StatusCode = StatusCodes.Status200OK, Body = "{}"u8.ToArray() },
                CacheJsonOpts),
            new DistributedCacheEntryOptions());

        var seeded = NewContext(provider, new MemoryStream());
        await filter.InvokeAsync(
            new TestFilterContext(seeded),
            _ => throw new InvalidOperationException("sanity: a valid entry at this key must replay"));
        seeded.Response.Headers.ContainsKey("Idempotency-Replayed").ShouldBeTrue(
            "sanity: this is the key the filter probes");

        await cache.SetAsync(storedKey, "{ this is not the cached shape"u8.ToArray(), new DistributedCacheEntryOptions());

        int executions = 0;
        var context = NewContext(provider, new MemoryStream());
        await filter.InvokeAsync(
            new TestFilterContext(context),
            _ =>
            {
                executions++;
                return ValueTask.FromResult<object?>(TypedResults.Ok(new SampleDto(Guid.NewGuid(), "widget")));
            });

        executions.ShouldBe(
            1,
            "an entry written by another version or another writer at the same key must degrade to a cache " +
            "miss; letting JsonException escape turns a shared-cache accident into a 500 on every retry.");
    }

    // ─── one key reused across two endpoints must not replay the other's response ────────

    [Fact]
    public async Task Filter_Should_NotReplayAcrossEndpoints_When_SameKeyIsReused()
    {
        var provider = BuildProvider();
        var filter = new IdempotencyEndpointFilter();
        var ticketId = Guid.NewGuid();

        var onTickets = NewContext(provider, new MemoryStream());
        onTickets.SetEndpoint(RouteEndpointFor("api/v1/tickets"));
        await filter.InvokeAsync(
            new TestFilterContext(onTickets),
            _ => ValueTask.FromResult<object?>(TypedResults.Created($"/tickets/{ticketId}", new SampleDto(ticketId, "ticket"))));

        int executions = 0;
        var onBrands = NewContext(provider, new MemoryStream());
        onBrands.SetEndpoint(RouteEndpointFor("api/v1/catalog/brands"));
        await filter.InvokeAsync(
            new TestFilterContext(onBrands),
            _ =>
            {
                executions++;
                return ValueTask.FromResult<object?>(TypedResults.Created("/brands/1", new SampleDto(Guid.NewGuid(), "brand")));
            });

        executions.ShouldBe(
            1,
            "keyed on tenant + key alone, a key reused against a second idempotent endpoint replays the " +
            "first endpoint's response and the second request silently never runs. 31 endpoints in this " +
            "repo share that namespace, and one of them is anonymous (self-registration, no tenant claim).");
        onBrands.Response.Headers.ContainsKey("Idempotency-Replayed").ShouldBeFalse();
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

    private static RouteEndpoint RouteEndpointFor(string pattern) => new(
        _ => Task.CompletedTask,
        RoutePatternFactory.Parse(pattern),
        order: 0,
        new EndpointMetadataCollection(),
        displayName: pattern);

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
