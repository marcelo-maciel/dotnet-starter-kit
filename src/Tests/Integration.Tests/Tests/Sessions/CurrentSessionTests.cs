using System.Security.Claims;
using Finbuckle.MultiTenant;
using Finbuckle.MultiTenant.Abstractions;
using FSH.Framework.Shared.Multitenancy;
using FSH.Modules.Identity.Contracts.DTOs;
using FSH.Modules.Identity.Contracts.Services;
using Integration.Tests.Infrastructure;

namespace Integration.Tests.Tests.Sessions;

/// <summary>
/// "Este dispositivo" on the caller's own session list: <c>GET /sessions/me</c> marks the session the
/// access token was issued for, and only that one. Each test seeds its own user so sessions left behind by
/// other classes (all of them log in as the root admin) cannot make a marking ambiguous. A token naming a revoked
/// session never reaches the list, nor does a client's token without a session id: the PAF session check answers
/// 401 first, so the users here are root's.
/// </summary>
[Collection(FshCollectionDefinition.Name)]
public sealed class CurrentSessionTests
{
    private readonly FshWebApplicationFactory _factory;
    private readonly AuthHelper _auth;

    public CurrentSessionTests(FshWebApplicationFactory factory)
    {
        _factory = factory;
        _auth = new AuthHelper(factory);
    }

    #region Happy Path

    [Fact]
    public async Task GetMySessions_Should_MarkOnlyTheSessionOfEachLogin_When_SameUserLogsInTwice()
    {
        // Arrange
        using var adminClient = await _auth.CreateRootAdminClientAsync();
        var user = await IdentityUserSeeder.CreateLoginableUserAsync(_factory, adminClient, "current-two");
        var firstLogin = await _auth.GetTokenAsync(user.Email, user.Password, TestConstants.RootTenantId);
        var secondLogin = await _auth.GetTokenAsync(user.Email, user.Password, TestConstants.RootTenantId);

        // Act
        var seenByFirst = await GetMySessionsAsync(firstLogin.AccessToken);
        var seenBySecond = await GetMySessionsAsync(secondLogin.AccessToken);

        // Assert
        seenByFirst.Count.ShouldBe(2);
        seenBySecond.Count.ShouldBe(2);
        var firstCurrent = seenByFirst.Single(s => s.IsCurrentSession);
        var secondCurrent = seenBySecond.Single(s => s.IsCurrentSession);
        firstCurrent.Id.ShouldNotBe(secondCurrent.Id);
        firstCurrent.CreatedAt.ShouldBeLessThan(secondCurrent.CreatedAt);
    }

    [Fact]
    public async Task GetMySessions_Should_KeepMarkingTheSameSession_When_TokenIsRefreshed()
    {
        // Arrange
        using var adminClient = await _auth.CreateRootAdminClientAsync();
        var user = await IdentityUserSeeder.CreateLoginableUserAsync(_factory, adminClient, "current-refresh");
        var login = await _auth.GetTokenAsync(user.Email, user.Password, TestConstants.RootTenantId);
        var beforeRefresh = (await GetMySessionsAsync(login.AccessToken)).Single(s => s.IsCurrentSession);

        // Act
        var refreshed = await RefreshAsync(login);
        var afterRefresh = await GetMySessionsAsync(refreshed.Token);

        // Assert
        afterRefresh.Count.ShouldBe(1);
        afterRefresh.Single(s => s.IsCurrentSession).Id.ShouldBe(beforeRefresh.Id);
    }

    #endregion

    #region Edge Cases

    [Fact]
    public async Task GetMySessions_Should_MarkNoSession_When_TokenCarriesNoSessionId()
    {
        // Arrange - a token shaped like the ones issued before the session id existed
        using var adminClient = await _auth.CreateRootAdminClientAsync();
        var user = await IdentityUserSeeder.CreateLoginableUserAsync(_factory, adminClient, "current-legacy");
        _ = await _auth.GetTokenAsync(user.Email, user.Password, TestConstants.RootTenantId);
        var legacyToken = await IssueTokenOutsideLoginAsync(user.UserId, extraClaims: []);

        // Act
        var sessions = await GetMySessionsAsync(legacyToken);

        // Assert
        sessions.Count.ShouldBe(1);
        sessions.ShouldAllBe(s => !s.IsCurrentSession);
    }

    [Fact]
    public async Task GetMySessions_Should_MarkNoSession_When_SessionIdIsMalformed()
    {
        // Arrange
        using var adminClient = await _auth.CreateRootAdminClientAsync();
        var user = await IdentityUserSeeder.CreateLoginableUserAsync(_factory, adminClient, "current-malformed");
        _ = await _auth.GetTokenAsync(user.Email, user.Password, TestConstants.RootTenantId);
        var malformedToken = await IssueTokenOutsideLoginAsync(user.UserId, extraClaims: [new Claim("sid", "not-a-guid")]);

        // Act
        var sessions = await GetMySessionsAsync(malformedToken);

        // Assert
        sessions.Count.ShouldBe(1);
        sessions.ShouldAllBe(s => !s.IsCurrentSession);
    }

    #endregion

    // ─── helpers ────────────────────────────────────────────────────────

    private async Task<List<UserSessionDto>> GetMySessionsAsync(string accessToken)
    {
        using var client = ClientWithBearer(accessToken);
        var response = await client.GetAsync($"{TestConstants.IdentityBasePath}/sessions/me");
        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.ShouldBe(HttpStatusCode.OK, $"Content: {content}");
        var sessions = await response.Content.ReadFromJsonAsync<List<UserSessionDto>>();
        sessions.ShouldNotBeNull();
        return sessions;
    }

    private async Task<TokenRefreshResult> RefreshAsync(TokenResult login)
    {
        using var client = _factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Post, $"{TestConstants.IdentityBasePath}/token/refresh");
        request.Headers.Add("tenant", TestConstants.RootTenantId);
        request.Content = JsonContent.Create(new { token = login.AccessToken, refreshToken = login.RefreshToken });
        var response = await client.SendAsync(request);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var refreshed = await response.Content.ReadFromJsonAsync<TokenRefreshResult>();
        refreshed.ShouldNotBeNull();
        return refreshed;
    }

    private async Task<string> IssueTokenOutsideLoginAsync(string userId, Claim[] extraClaims)
    {
        using var scope = _factory.Services.CreateScope();
        var tenantInfo = await scope.ServiceProvider
            .GetRequiredService<IMultiTenantStore<AppTenantInfo>>()
            .GetAsync(TestConstants.RootTenantId);
        scope.ServiceProvider.GetRequiredService<IMultiTenantContextSetter>()
            .MultiTenantContext = new MultiTenantContext<AppTenantInfo>(tenantInfo);

        var built = await scope.ServiceProvider.GetRequiredService<IIdentityService>()
            .BuildClaimsForUserAsync(userId, TestConstants.RootTenantId, CancellationToken.None);
        built.ShouldNotBeNull();
        var (subject, claims) = built.Value;
        var token = await scope.ServiceProvider.GetRequiredService<ITokenService>()
            .IssueAsync(subject, claims.Concat(extraClaims), TestConstants.RootTenantId, CancellationToken.None);
        return token.AccessToken;
    }

    private HttpClient ClientWithBearer(string accessToken)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        client.DefaultRequestHeaders.Add("tenant", TestConstants.RootTenantId);
        return client;
    }
}
