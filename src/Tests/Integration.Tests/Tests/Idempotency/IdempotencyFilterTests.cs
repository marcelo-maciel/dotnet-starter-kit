using System.Net.Http.Json;
using Integration.Tests.Infrastructure;

namespace Integration.Tests.Tests.Idempotency;

[Collection(FshCollectionDefinition.Name)]
public sealed class IdempotencyFilterTests
{
    private const string IdempotencyHeader = "Idempotency-Key";
    private const string ReplayedHeader = "Idempotency-Replayed";

    private readonly AuthHelper _auth;

    public IdempotencyFilterTests(FshWebApplicationFactory factory)
    {
        _auth = new AuthHelper(factory);
    }

    // These cover the wiring: Idempotency-Replayed presence/absence, and that a distinct key forces fresh
    // execution. Replay of the exact body — no longer out of reach, the filter buffers the executed result
    // instead of caching the raw IResult — is asserted end-to-end against real Redis in
    // CreateBillingPlan_Should_ReplaySameResponse_When_IdempotencyKeyReused below, and branch by branch
    // in Framework.Tests/Web/IdempotencyEndpointFilterReplayTests.

    [Fact]
    public async Task CreateBillingPlan_Should_ExecuteNormally_When_NoIdempotencyKey()
    {
        using var client = await _auth.CreateRootAdminClientAsync();
        var uniqueId = Guid.NewGuid().ToString("N")[..8];
        var payload = new
        {
            key = $"noidem-{uniqueId}",
            name = $"NoIdem Plan {uniqueId}",
            currency = "USD",
            monthlyBasePrice = 5m
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/billing/plans")
        {
            Content = JsonContent.Create(payload)
        };
        var response = await client.SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.Contains(ReplayedHeader).ShouldBeFalse(
            "A request without an idempotency key must never be marked as replayed.");
    }

    [Fact]
    public async Task CreateBillingPlan_Should_ExecuteSecondCall_When_DifferentIdempotencyKey()
    {
        using var client = await _auth.CreateRootAdminClientAsync();
        var uniqueId = Guid.NewGuid().ToString("N")[..8];
        var firstKey = $"idem-a-{uniqueId}";
        var secondKey = $"idem-b-{uniqueId}";
        var basePayload = new
        {
            key = $"diff-{uniqueId}",
            name = $"Diff Plan {uniqueId}",
            currency = "USD",
            monthlyBasePrice = 1m
        };

        using var firstRequest = BuildRequest(basePayload, firstKey);
        (await client.SendAsync(firstRequest)).StatusCode.ShouldBe(HttpStatusCode.OK);

        // Different key + different plan-key so it's a legitimately new resource.
        var secondPayload = basePayload with { key = $"diff-b-{uniqueId}" };
        using var secondRequest = BuildRequest(secondPayload, secondKey);
        var secondResponse = await client.SendAsync(secondRequest);

        secondResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        secondResponse.Headers.Contains(ReplayedHeader).ShouldBeFalse(
            "A different idempotency key must route through a fresh execution.");
    }

    [Fact]
    public async Task CreateBillingPlan_Should_ReplaySameResponse_When_IdempotencyKeyReused()
    {
        using var client = await _auth.CreateRootAdminClientAsync();
        var uniqueId = Guid.NewGuid().ToString("N")[..8];
        var key = $"idem-replay-{uniqueId}";
        var payload = new
        {
            key = $"replay-{uniqueId}",
            name = $"Replay Plan {uniqueId}",
            currency = "USD",
            monthlyBasePrice = 2m
        };

        using var firstRequest = BuildRequest(payload, key);
        using var first = await client.SendAsync(firstRequest);
        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        var firstBody = await first.Content.ReadAsStringAsync();

        // Plan keys are unique, so a second execution would fail instead of answering 200 with the same id.
        using var secondRequest = BuildRequest(payload, key);
        using var second = await client.SendAsync(secondRequest);
        second.StatusCode.ShouldBe(HttpStatusCode.OK);
        second.Headers.Contains(ReplayedHeader).ShouldBeTrue(
            "Second call with the same idempotency key must carry the Idempotency-Replayed header.");
        (await second.Content.ReadAsStringAsync()).ShouldBe(firstBody, "Replay must return the exact first body.");
    }

    private static HttpRequestMessage BuildRequest(object payload, string idempotencyKey)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/billing/plans")
        {
            Content = JsonContent.Create(payload)
        };
        request.Headers.Add(IdempotencyHeader, idempotencyKey);
        return request;
    }
}
