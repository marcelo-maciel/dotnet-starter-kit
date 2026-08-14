using FSH.Framework.Eventing.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FSH.Framework.Eventing.Outbox;

/// <summary>
/// Background service that periodically dispatches outbox messages.
/// Alternative to Hangfire-based scheduling for simpler deployments.
/// </summary>
public sealed partial class OutboxDispatcherHostedService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<OutboxDispatcherHostedService> _logger;
    private readonly TimeSpan _interval;

    public OutboxDispatcherHostedService(
        IServiceScopeFactory scopeFactory,
        IOptions<EventingOptions> options,
        ILogger<OutboxDispatcherHostedService> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _scopeFactory = scopeFactory;
        _logger = logger;
        _interval = TimeSpan.FromSeconds(options.Value.OutboxDispatchIntervalSeconds > 0
            ? options.Value.OutboxDispatchIntervalSeconds
            : 10);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogServiceStarted(_interval.TotalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await DispatchOutboxAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Graceful shutdown
                break;
            }
            // Broad catch is intentional: the hosted service loop must not crash
            // due to transient errors; failures are logged and the next cycle retries.
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error dispatching outbox messages");
            }

            try
            {
                await Task.Delay(_interval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }

        _logger.LogInformation("Outbox dispatcher hosted service stopped");
    }

    /// <summary>
    /// Drains one cycle: every database that can hold outbox rows, not just the default one.
    /// A tenant with a dedicated connection string writes its rows into its own database, so a
    /// single-database poll leaves them undispatched forever.
    /// </summary>
    /// <remarks>Internal rather than private so the per-cycle behaviour is testable without timing.</remarks>
    internal async Task DispatchOutboxAsync(CancellationToken ct)
    {
        // Resolved per cycle: tenants — and therefore databases — can be added at runtime.
        using var providerScope = _scopeFactory.CreateScope();
        var targetProvider = providerScope.ServiceProvider.GetRequiredService<IEventingDrainTargetProvider>();
        var drainScope = providerScope.ServiceProvider.GetRequiredService<IEventingDrainScope>();

        var targets = await targetProvider.GetTargetsAsync(ct).ConfigureAwait(false);

        foreach (var target in targets)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                // The tenant context must be installed BEFORE the scope's EventingDbContext is
                // constructed — BaseDbContext captures TenantInfo, and with it the connection
                // string, at construction time.
                using (drainScope.Begin(target))
                {
                    using var scope = _scopeFactory.CreateScope();
                    var dispatcher = scope.ServiceProvider.GetRequiredService<OutboxDispatcher>();
                    await dispatcher.DispatchAsync(ct).ConfigureAwait(false);
                }
            }
            // Broad catch is intentional: one unreachable tenant database must not stop the
            // others from draining this cycle.
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                LogDrainFailed(ex, target.TenantId ?? "(default)");
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Outbox dispatcher hosted service started. Dispatch interval: {Interval}s")]
    private partial void LogServiceStarted(double interval);

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to drain the outbox for tenant {TenantId}")]
    private partial void LogDrainFailed(Exception exception, string tenantId);
}