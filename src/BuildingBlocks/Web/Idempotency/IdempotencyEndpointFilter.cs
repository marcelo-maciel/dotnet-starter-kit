using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FSH.Framework.Caching;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using StackExchange.Redis;

namespace FSH.Framework.Web.Idempotency;

/// <summary>
/// Endpoint filter that provides idempotency for POST/PUT/PATCH requests.
/// When an Idempotency-Key header is present, the response is cached and replayed
/// for subsequent requests with the same key.
/// </summary>
/// <remarks>
/// Uses <see cref="IDistributedCache"/> for both the probe read and the write, on the same raw key
/// and serializer, so the two are symmetric (a HybridCache write keys its L2 entries under its own
/// scheme, which a raw-key probe never finds — replay then silently never engages).
/// The handler result is executed into a buffer so the cached payload is the real wire body and
/// status code (an <c>Ok&lt;T&gt;</c>/<c>Created&lt;T&gt;</c> wrapper would otherwise be serialized
/// verbatim, and <c>Response.StatusCode</c> is still the default at filter time — the IResult sets
/// it only when it executes). Concurrent duplicate keys are serialized by an atomic in-flight
/// reservation (Redis <c>SET NX</c> when a multiplexer is registered, an in-process set otherwise).
/// The stored response is written before the body reaches the client and with a token that cannot be
/// cancelled: it is the durable record that the side effect already happened, so it has to outlive the
/// request that produced it — a client that times out and retries is the commonest duplicate there is.
/// </remarks>
public sealed class IdempotencyEndpointFilter : IEndpointFilter
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    // Response headers worth replaying. Allow-list rather than block-list: executing an IResult is
    // exactly when these get set (Created(uri, value) writes Location), and a replayed 201 without
    // Location breaks any client that follows it — under retry conditions nobody tests. Everything
    // else is either transport (Content-Length, Transfer-Encoding) or host-owned (Date, Server), and
    // replaying a stale value there corrupts the response.
    private static readonly string[] ReplayableHeaders = ["Location", "ETag"];

    // In-process reservation used when no Redis multiplexer is registered. Single-instance only —
    // a multi-instance host in this stack already runs Redis (shared Data Protection key ring), so
    // the Redis branch below covers every deployment where cross-instance duplicates are possible.
    private static readonly ConcurrentDictionary<string, byte> InFlight = new(StringComparer.Ordinal);

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var httpContext = context.HttpContext;
        var options = httpContext.RequestServices.GetRequiredService<IOptions<IdempotencyOptions>>().Value;
        var idempotencyKey = httpContext.Request.Headers[options.HeaderName].ToString();

        // No header = pass through (idempotency is opt-in per request)
        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            return await next(context).ConfigureAwait(false);
        }

        if (idempotencyKey.Length > options.MaxKeyLength)
        {
            return TypedResults.BadRequest($"Idempotency key exceeds maximum length of {options.MaxKeyLength}.");
        }

        var distributedCache = httpContext.RequestServices.GetRequiredService<IDistributedCache>();
        var logger = httpContext.RequestServices.GetRequiredService<ILogger<IdempotencyEndpointFilter>>();

        // Include tenant context in cache key for isolation
        var tenantId = httpContext.User.FindFirst("tenant")?.Value ?? "global";
        var cacheKey = CacheKeys.IdempotencyEntry(tenantId, idempotencyKey);

        // Probe-only read via IDistributedCache (real GetAsync, null on miss — unlike HybridCache's
        // factory). Bypasses L1: replays are rare vs first-calls, so L1 warmth has little value.
        var cached = await ProbeAsync(distributedCache, cacheKey, httpContext.RequestAborted).ConfigureAwait(false);
        if (cached is not null)
        {
            return await ReplayAsync(httpContext, cached, idempotencyKey, logger).ConfigureAwait(false);
        }

        // Atomically reserve the key so concurrent duplicates don't both execute the handler.
        var multiplexer = httpContext.RequestServices.GetService<IConnectionMultiplexer>();
        var reservationKey = cacheKey + ":inflight";
        if (!await TryReserveAsync(multiplexer, reservationKey, options.ReservationTtl, logger, idempotencyKey).ConfigureAwait(false))
        {
            // Another request with this key is in flight. It may have finished between the probe
            // and the reservation — re-probe once, otherwise report the in-progress conflict.
            var raced = await ProbeAsync(distributedCache, cacheKey, httpContext.RequestAborted).ConfigureAwait(false);
            return raced is not null
                ? await ReplayAsync(httpContext, raced, idempotencyKey, logger).ConfigureAwait(false)
                : TypedResults.Conflict("A request with this Idempotency-Key is already being processed.");
        }

        try
        {
            var result = await next(context).ConfigureAwait(false);

            // Execute the result into a buffer to capture the real wire body + status code, then
            // serve that buffer to the client. Returning the IResult unexecuted would leave
            // Response.StatusCode at its default and cache the wrapper object, not the wire body.
            var captured = await ExecuteAndCaptureAsync(result, httpContext).ConfigureAwait(false);

            httpContext.Response.StatusCode = captured.StatusCode;
            if (captured.ContentType is not null)
            {
                httpContext.Response.ContentType = captured.ContentType;
            }

            // Store BEFORE the body goes to the client, and only on success. The handler's side effect
            // has already committed at this point, so the record of it must not depend on the client
            // still being there; writing to a socket the client closed throws, and doing that first
            // would skip the store and let the retry re-execute the handler. Non-2xx is not a record
            // of a committed side effect — caching it would lock the key out for the full TTL after a
            // transient downstream failure, so a retry with the same key is allowed to run again.
            if (captured.StatusCode is >= 200 and < 300)
            {
                await CacheResponseAsync(distributedCache, cacheKey, captured, options.DefaultTtl, logger, idempotencyKey).ConfigureAwait(false);
            }

            if (captured.Body.Length > 0)
            {
                await httpContext.Response.Body.WriteAsync(captured.Body, httpContext.RequestAborted).ConfigureAwait(false);
            }

            // Response already written to the body directly; return an empty result so the framework
            // doesn't serialize a null return and append "null" after the captured payload.
            return Results.Empty;
        }
        finally
        {
            await ReleaseReservationAsync(multiplexer, reservationKey, logger, idempotencyKey).ConfigureAwait(false);
        }
    }

    // Write to the SAME store + key the probe reads. HybridCache.SetAsync keys its L2 entries under
    // its own scheme, so a raw-key IDistributedCache probe never found them and replay silently never
    // engaged. Idempotency entries are short-lived (TTL) and their tag-purge path was unused, so
    // IDistributedCache alone — symmetric with the probe — is correct.
    private static async ValueTask CacheResponseAsync(
        IDistributedCache distributedCache,
        string cacheKey,
        CachedIdempotentResponse response,
        TimeSpan ttl,
        ILogger logger,
        string idempotencyKey)
    {
        try
        {
            var payload = JsonSerializer.SerializeToUtf8Bytes(response, JsonOpts);

            // CancellationToken.None on purpose: RequestAborted is already signalled whenever this
            // matters (client hung up), and cancelling the store is what makes the retry re-execute.
            await distributedCache.SetAsync(
                cacheKey,
                payload,
                new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = ttl },
                CancellationToken.None).ConfigureAwait(false);
        }
        // Best-effort caching: a store that is down degrades idempotency to a convenience rather
        // than 500ing a request whose side effect already committed. The token above is None, so
        // an OperationCanceledException here is not a client disconnect and is left to propagate.
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to cache idempotent response for key {KeyHash}", HashKey(idempotencyKey));
        }
    }

    private static async ValueTask<CachedIdempotentResponse?> ProbeAsync(
        IDistributedCache cache, string cacheKey, CancellationToken ct)
    {
        var bytes = await cache.GetAsync(cacheKey, ct).ConfigureAwait(false);
        return bytes is { Length: > 0 }
            ? JsonSerializer.Deserialize<CachedIdempotentResponse>(bytes, JsonOpts)
            : null;
    }

    private static async ValueTask<object?> ReplayAsync(
        HttpContext httpContext, CachedIdempotentResponse cached, string idempotencyKey, ILogger logger)
    {
        if (logger.IsEnabled(LogLevel.Debug))
        {
            logger.LogDebug("Idempotent replay for key {KeyHash}", HashKey(idempotencyKey));
        }

        httpContext.Response.Headers["Idempotency-Replayed"] = "true";
        httpContext.Response.StatusCode = cached.StatusCode;
        if (cached.ContentType is not null)
        {
            httpContext.Response.ContentType = cached.ContentType;
        }

        foreach (var header in cached.Headers)
        {
            httpContext.Response.Headers[header.Key] = header.Value;
        }

        if (cached.Body.Length > 0)
        {
            await httpContext.Response.Body.WriteAsync(cached.Body, httpContext.RequestAborted).ConfigureAwait(false);
        }

        // Empty result (not null) so the framework doesn't append a serialized "null".
        return Results.Empty;
    }

    private static async Task<CachedIdempotentResponse> ExecuteAndCaptureAsync(
        object? result, HttpContext httpContext)
    {
        var originalBody = httpContext.Response.Body;
        var originalAborted = httpContext.RequestAborted;
        await using var buffer = new MemoryStream();
        httpContext.Response.Body = buffer;

        // Detach the client's abort token while capturing. The result is being written to an
        // in-memory buffer, never the socket, so a client that hung up must not truncate it — and
        // ASP.NET's WriteAsJsonAsync reads RequestAborted itself and swallows the cancellation, so
        // the capture would silently come back EMPTY and that empty body would be cached and
        // replayed for the full TTL.
        httpContext.RequestAborted = CancellationToken.None;
        try
        {
            switch (result)
            {
                case null:
                    break;
                case IResult endpointResult:
                    await endpointResult.ExecuteAsync(httpContext).ConfigureAwait(false);
                    break;
                default:
                    // A non-IResult return is serialized as JSON by the framework — mirror that.
                    await httpContext.Response.WriteAsJsonAsync(result, result.GetType(), options: null, contentType: null, CancellationToken.None).ConfigureAwait(false);
                    break;
            }

            var statusCode = httpContext.Response.StatusCode is > 0 and < 600
                ? httpContext.Response.StatusCode
                : StatusCodes.Status200OK;

            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var name in ReplayableHeaders)
            {
                var value = httpContext.Response.Headers[name];
                if (!StringValues.IsNullOrEmpty(value))
                {
                    headers[name] = value.ToString();
                }
            }

            return new CachedIdempotentResponse
            {
                StatusCode = statusCode,
                ContentType = httpContext.Response.ContentType ?? "application/json",
                Body = buffer.ToArray(),
                Headers = headers,
            };
        }
        finally
        {
            httpContext.Response.Body = originalBody;
            httpContext.RequestAborted = originalAborted;
        }
    }

    private static async ValueTask<bool> TryReserveAsync(
        IConnectionMultiplexer? multiplexer, string reservationKey, TimeSpan ttl, ILogger logger, string idempotencyKey)
    {
        if (multiplexer is not null)
        {
            try
            {
                var db = multiplexer.GetDatabase();
                return await db.StringSetAsync(reservationKey, "1", ttl, When.NotExists).ConfigureAwait(false);
            }
            // Fail open on a Redis blip: the reservation is a concurrency convenience, not a correctness
            // requirement (the response cache still dedups later retries). Proceed rather than 500 the
            // request, matching the best-effort stance the response write already takes.
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Idempotency reservation failed for key {KeyHash}; proceeding without it", HashKey(idempotencyKey));
                return true;
            }
        }

        return InFlight.TryAdd(reservationKey, 0);
    }

    private static async ValueTask ReleaseReservationAsync(IConnectionMultiplexer? multiplexer, string reservationKey, ILogger logger, string idempotencyKey)
    {
        if (multiplexer is not null)
        {
            try
            {
                await multiplexer.GetDatabase().KeyDeleteAsync(reservationKey).ConfigureAwait(false);
            }
            // Best-effort release: a Redis fault here must not throw out of the finally. The short
            // ReservationTtl expires the key anyway, so a missed delete self-heals in seconds.
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Failed to release idempotency reservation for key {KeyHash}", HashKey(idempotencyKey));
            }

            return;
        }

        InFlight.TryRemove(reservationKey, out _);
    }

    private static string HashKey(string key)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return Convert.ToHexString(hash.AsSpan(0, 8));
    }
}

public static class IdempotencyEndpointExtensions
{
    /// <summary>
    /// Enables idempotency for this endpoint. Requires Idempotency-Key header on requests.
    /// Duplicate requests with the same key return the cached response.
    /// </summary>
    public static RouteHandlerBuilder WithIdempotency(this RouteHandlerBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.AddEndpointFilter<IdempotencyEndpointFilter>();
    }
}
