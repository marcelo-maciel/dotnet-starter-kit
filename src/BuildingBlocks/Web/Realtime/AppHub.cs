using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace FSH.Framework.Web.Realtime;

/// <summary>
/// Single shared SignalR hub for app-wide realtime: presence and notifications. Modules don't
/// depend on this hub directly — they emit through <see cref="IHubContext{AppHub}"/> and target
/// the well-known SignalR groups.
///
/// Group naming convention:
/// <list type="bullet">
///   <item><c>user:{userId}</c> — every connection a user has open. Used for per-user pushes
///   (notifications).</item>
///   <item><c>tenant:{tenantId}</c> — every connection of the tenant. Used for presence.</item>
/// </list>
/// </summary>
[Authorize]
public sealed class AppHub : Hub
{
    private readonly IPresenceTracker _presence;
    private readonly ILogger<AppHub> _logger;

    public AppHub(
        IPresenceTracker presence,
        ILogger<AppHub> logger)
    {
        _presence = presence;
        _logger = logger;
    }

    /// <summary>
    /// Reads the authenticated user id off the connection's principal. Cannot use
    /// <c>ICurrentUser</c> here because it resolves through <c>IHttpContextAccessor</c> — the
    /// originating negotiate <c>HttpContext</c> is not pinned to subsequent hub method invocations,
    /// so any indirection through it returns nulls.
    /// </summary>
    private string? GetUserId()
    {
        var user = Context.User;
        if (user?.Identity?.IsAuthenticated != true) return null;
        return user.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? user.FindFirstValue("sub")
            ?? user.FindFirstValue("uid");
    }

    /// <summary>
    /// Reads the tenant id off the principal — used to scope cross-tenant
    /// broadcasts (presence) to a single tenant group so a 1000-user tenant
    /// doesn't broadcast every connect to other tenants.
    /// </summary>
    private string? GetTenantId()
    {
        var user = Context.User;
        if (user is null) return null;
        return user.FindFirstValue("tenant")
            ?? user.FindFirstValue("tid")
            ?? user.FindFirstValue("tenantId");
    }

    public override async Task OnConnectedAsync()
    {
        try
        {
            var userId = GetUserId();
            if (string.IsNullOrEmpty(userId) || userId == Guid.Empty.ToString())
            {
                Context.Abort();
                return;
            }

            await Groups.AddToGroupAsync(Context.ConnectionId, $"user:{userId}", Context.ConnectionAborted)
                .ConfigureAwait(false);

            // Join the tenant group — scopes cross-tenant broadcasts (presence) so a 1000-user
            // tenant doesn't broadcast every connect to other tenants.
            var tenantId = GetTenantId();
            if (!string.IsNullOrEmpty(tenantId))
            {
                await Groups.AddToGroupAsync(Context.ConnectionId, $"tenant:{tenantId}", Context.ConnectionAborted)
                    .ConfigureAwait(false);
            }

            AppHubLog.Connected(_logger, Context.ConnectionId, userId);

            // On the user's first open connection, broadcast PresenceChanged so clients flip the dot.
            // Scoped to the tenant group, not Clients.All, to avoid global fan-out.
            if (_presence.Connect(userId))
            {
                var target = string.IsNullOrEmpty(tenantId)
                    ? Clients.All
                    : Clients.Group($"tenant:{tenantId}");
                await target.SendAsync(
                        "PresenceChanged",
                        new { userId, online = true },
                        Context.ConnectionAborted)
                    .ConfigureAwait(false);
            }

            await base.OnConnectedAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (Context.ConnectionAborted.IsCancellationRequested)
        {
            // Client disconnected mid-connect (fast reconnect, page navigation, negotiate/connect
            // churn). The aborting token cancels the in-flight group joins. There's
            // no connection left to set up, so this is expected — swallow it rather than let it
            // surface as a hub-dispatch error in the logs.
        }
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var userId = GetUserId();
        if (!string.IsNullOrEmpty(userId) && _presence.Disconnect(userId))
        {
            var tenantId = GetTenantId();
            var target = string.IsNullOrEmpty(tenantId)
                ? Clients.All
                : Clients.Group($"tenant:{tenantId}");
            await target.SendAsync(
                    "PresenceChanged",
                    new { userId, online = false })
                .ConfigureAwait(false);
        }

        await base.OnDisconnectedAsync(exception).ConfigureAwait(false);
    }
}

internal static partial class AppHubLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Debug,
        Message = "AppHub connection {ConnectionId} for user {UserId} joined its user and tenant groups")]
    public static partial void Connected(ILogger logger, string connectionId, string userId);
}
