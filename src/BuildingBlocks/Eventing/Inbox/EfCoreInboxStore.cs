using FSH.Framework.Eventing.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FSH.Framework.Eventing.Inbox;

/// <summary>
/// EF Core inbox store over the framework-owned <see cref="EventingDbContext"/>.
/// Non-generic for the same reason as <see cref="Outbox.EfCoreOutboxStore"/>: a
/// per-DbContext generic store meant one non-keyed <c>IInboxStore</c> registration
/// per module, so .NET DI silently redirected every module's idempotency writes to
/// whichever context registered last (issue #1349).
/// </summary>
public sealed class EfCoreInboxStore : IInboxStore
{
    private readonly EventingDbContext _dbContext;
    private readonly TimeProvider _timeProvider;

    public EfCoreInboxStore(EventingDbContext dbContext, TimeProvider timeProvider)
    {
        _dbContext = dbContext;
        _timeProvider = timeProvider;
    }

    public async Task<bool> HasProcessedAsync(Guid eventId, string handlerName, CancellationToken ct = default)
    {
        return await _dbContext.Set<InboxMessage>()
            .AnyAsync(i => i.Id == eventId && i.HandlerName == handlerName, ct)
            .ConfigureAwait(false);
    }

    public async Task MarkProcessedAsync(Guid eventId, string handlerName, string? tenantId, string eventType, CancellationToken ct = default)
    {
        // Idempotent: skip if already marked (race between direct publish and outbox retry)
        bool alreadyProcessed = await _dbContext.Set<InboxMessage>()
            .AnyAsync(i => i.Id == eventId && i.HandlerName == handlerName, ct)
            .ConfigureAwait(false);

        if (alreadyProcessed)
        {
            return;
        }

        var message = new InboxMessage
        {
            Id = eventId,
            EventType = eventType,
            HandlerName = handlerName,
            TenantId = tenantId,
            ProcessedOnUtc = _timeProvider.GetUtcNow().UtcDateTime
        };

        _dbContext.Set<InboxMessage>().Add(message);

        try
        {
            await _dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateException) when (!ct.IsCancellationRequested)
        {
            // Concurrent insert won the race — treat as already processed.
            _dbContext.ChangeTracker.Clear();
        }
    }
}