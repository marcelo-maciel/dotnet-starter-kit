using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FSH.Framework.Caching;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace FSH.Framework.Web.Idempotency;

/// <summary>
/// Endpoint filter that provides idempotency for POST/PUT/PATCH requests.
/// When an Idempotency-Key header is present, the response is cached and replayed
/// for subsequent requests with the same key.
/// </summary>
/// <remarks>
/// Uses <see cref="IDistributedCache"/> directly for the probe read (bypassing
/// <see cref="HybridCache"/>'s factory-mandatory API) and <see cref="HybridCache.SetAsync"/>
/// for the write path so replays benefit from L1 and the regular tag invalidation story.
/// The handler result is executed into a buffer so the cached payload is the real wire body and
/// status code (an <c>Ok&lt;T&gt;</c>/<c>Created&lt;T&gt;</c> wrapper would otherwise be serialized
/// verbatim, and <c>Response.StatusCode</c> is still the default at filter time — the IResult sets
/// it only when it executes). Concurrent duplicate keys are serialized by an atomic in-flight
/// reservation (Redis <c>SET NX</c> when a multiplexer is registered, an in-process set otherwise).
/// </remarks>
public sealed class IdempotencyEndpointFilter : IEndpointFilter
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

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
        var hybridCache = httpContext.RequestServices.GetRequiredService<HybridCache>();
        var logger = httpContext.RequestServices.GetRequiredService<ILogger<IdempotencyEndpointFilter>>();

        // Include tenant context in cache key for isolation
        var tenantId = httpContext.User.FindFirst("tenant")?.Value ?? "global";
        var cacheKey = CacheKeys.IdempotencyEntry(tenantId, idempotencyKey);
        var tags = new[] { CacheKeys.Tags.Idempotency, CacheKeys.Tags.Tenant(tenantId) };

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
        if (!await TryReserveAsync(multiplexer, reservationKey, options.DefaultTtl, httpContext.RequestAborted).ConfigureAwait(false))
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
            var (statusCode, contentType, body) = await ExecuteAndCaptureAsync(result, httpContext).ConfigureAwait(false);

            httpContext.Response.StatusCode = statusCode;
            if (contentType is not null)
            {
                httpContext.Response.ContentType = contentType;
            }

            if (body.Length > 0)
            {
                await httpContext.Response.Body.WriteAsync(body, httpContext.RequestAborted).ConfigureAwait(false);
            }

            // Cache the response through HybridCache so the tag invalidation path works for purges.
            try
            {
                var responseToCache = new CachedIdempotentResponse
                {
                    StatusCode = statusCode,
                    ContentType = contentType ?? "application/json",
                    Body = body,
                };

                var setOptions = new HybridCacheEntryOptions
                {
                    Expiration = options.DefaultTtl,
                    LocalCacheExpiration = options.DefaultTtl < TimeSpan.FromMinutes(2) ? options.DefaultTtl : TimeSpan.FromMinutes(2),
                };
                await hybridCache.SetAsync(cacheKey, responseToCache, setOptions, tags, httpContext.RequestAborted).ConfigureAwait(false);
            }
            // Best-effort caching: idempotency replay is a convenience, not a correctness requirement
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Failed to cache idempotent response for key {KeyHash}", HashKey(idempotencyKey));
            }

            // Response already written to the body directly; return an empty result so the framework
            // doesn't serialize a null return and append "null" after the captured payload.
            return Results.Empty;
        }
        finally
        {
            await ReleaseReservationAsync(multiplexer, reservationKey).ConfigureAwait(false);
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

        if (cached.Body.Length > 0)
        {
            await httpContext.Response.Body.WriteAsync(cached.Body, httpContext.RequestAborted).ConfigureAwait(false);
        }

        // Empty result (not null) so the framework doesn't append a serialized "null".
        return Results.Empty;
    }

    private static async Task<(int StatusCode, string? ContentType, byte[] Body)> ExecuteAndCaptureAsync(
        object? result, HttpContext httpContext)
    {
        var originalBody = httpContext.Response.Body;
        await using var buffer = new MemoryStream();
        httpContext.Response.Body = buffer;
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
                    await httpContext.Response.WriteAsJsonAsync(result, result.GetType(), options: null, contentType: null, httpContext.RequestAborted).ConfigureAwait(false);
                    break;
            }

            var statusCode = httpContext.Response.StatusCode is > 0 and < 600
                ? httpContext.Response.StatusCode
                : StatusCodes.Status200OK;
            return (statusCode, httpContext.Response.ContentType, buffer.ToArray());
        }
        finally
        {
            httpContext.Response.Body = originalBody;
        }
    }

    private static async ValueTask<bool> TryReserveAsync(
        IConnectionMultiplexer? multiplexer, string reservationKey, TimeSpan ttl, CancellationToken ct)
    {
        if (multiplexer is not null)
        {
            var db = multiplexer.GetDatabase();
            return await db.StringSetAsync(reservationKey, "1", ttl, When.NotExists).ConfigureAwait(false);
        }

        _ = ct;
        return InFlight.TryAdd(reservationKey, 0);
    }

    private static async ValueTask ReleaseReservationAsync(IConnectionMultiplexer? multiplexer, string reservationKey)
    {
        if (multiplexer is not null)
        {
            await multiplexer.GetDatabase().KeyDeleteAsync(reservationKey).ConfigureAwait(false);
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
