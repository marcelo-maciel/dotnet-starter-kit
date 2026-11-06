using System.Diagnostics;
using FSH.Framework.Caching;
using FSH.Framework.Core.Exceptions;
using FSH.Framework.Eventing.Abstractions;
using FSH.Modules.Identity.Contracts.Events;
using FSH.Modules.Identity.Contracts.Services;
using FSH.Modules.Identity.Contracts.v1.Impersonation;
using FSH.Modules.Identity.Data;
using FSH.Modules.Identity.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;

namespace FSH.Modules.Identity.Services;

internal sealed class ImpersonationGrantService(
    IdentityDbContext db,
    HybridCache cache,
    TimeProvider timeProvider,
    IOutboxWriter outbox) : IImpersonationGrantService
{
    public async Task<ImpersonationGrantDto> CreateAsync(CreateGrantInput input, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        var grant = ImpersonationGrant.Create(
            id: Guid.NewGuid(),
            jti: input.Jti,
            actorUserId: input.ActorUserId,
            actorUserName: input.ActorUserName,
            actorTenantId: input.ActorTenantId,
            impersonatedUserId: input.ImpersonatedUserId,
            impersonatedUserName: input.ImpersonatedUserName,
            impersonatedTenantId: input.ImpersonatedTenantId,
            reason: input.Reason,
            startedAtUtc: input.StartedAtUtc,
            expiresAtUtc: input.ExpiresAtUtc,
            clientId: input.ClientId,
            ipAddress: input.IpAddress,
            userAgent: input.UserAgent);

        db.ImpersonationGrants.Add(grant);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        // Prime the cache with status=Active so the JWT hook hits on the first request without a DB round-trip.
        // Entry self-evicts after the token lifetime; a miss after expiry is harmless since the hook treats expired tokens as ended.
        await SetCachedStatusAsync(grant, GrantState.Active, ct).ConfigureAwait(false);

        return ToDto(grant);
    }

    public async Task<ImpersonationGrantDto?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        var grant = await db.ImpersonationGrants
            .AsNoTracking()
            .FirstOrDefaultAsync(g => g.Id == id, ct)
            .ConfigureAwait(false);
        return grant is null ? null : ToDto(grant);
    }

    public async Task<ImpersonationGrantDto?> MarkEndedByJtiAsync(string jti, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNullOrWhiteSpace(jti);

        var (grant, terminated) = await TerminateAsync(
            c => LoadLockedByJtiAsync(jti, c),
            g => g.MarkEnded(timeProvider.GetUtcNow().UtcDateTime),
            ImpersonationGrantStatus.Ended,
            ct).ConfigureAwait(false);
        if (grant is null) return null;
        if (terminated)
        {
            await SetCachedStatusAsync(grant, GrantState.EndedOrRevoked, ct).ConfigureAwait(false);
        }

        return ToDto(grant);
    }

    public async Task<ImpersonationGrantDto> RevokeAsync(
        Guid id,
        string revokedByUserId,
        string? revokedByUserName,
        string? reason,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNullOrWhiteSpace(revokedByUserId);

        var (grant, terminated) = await TerminateAsync(
            c => LoadLockedByIdAsync(id, c),
            g => g.Revoke(
                revokedAtUtc: timeProvider.GetUtcNow().UtcDateTime,
                revokedByUserId: revokedByUserId,
                revokedByUserName: revokedByUserName,
                reason: reason),
            ImpersonationGrantStatus.Revoked,
            ct).ConfigureAwait(false);
        if (grant is null) throw new NotFoundException("impersonation grant not found");

        // An already-terminal grant is returned as is (idempotent). Otherwise the revocation marker is
        // written BEFORE returning so a racing request doesn't slip through with a cached Active marker.
        if (terminated)
        {
            await SetCachedStatusAsync(grant, GrantState.EndedOrRevoked, ct).ConfigureAwait(false);
        }

        return ToDto(grant);
    }

    public async Task<bool> IsRevokedOrEndedAsync(string jti, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(jti)) return false;

        var state = await cache.GetOrCreateAsync(
            CacheKeys.ImpersonationGrantStatus(jti),
            new FactoryState(db, jti),
            LoadStateAsync,
            options: CacheEntryOptions,
            cancellationToken: ct).ConfigureAwait(false);

        return state == GrantState.EndedOrRevoked || state == GrantState.Unknown;
    }

    public async Task<IReadOnlyList<ImpersonationGrantDto>> ListAsync(
        ImpersonationGrantStatus? status,
        string? impersonatedTenantId,
        string? actorUserId,
        int take,
        CancellationToken ct = default)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        IQueryable<ImpersonationGrant> q = db.ImpersonationGrants.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(impersonatedTenantId))
        {
            q = q.Where(g => g.ImpersonatedTenantId == impersonatedTenantId);
        }
        if (!string.IsNullOrWhiteSpace(actorUserId))
        {
            q = q.Where(g => g.ActorUserId == actorUserId);
        }
        if (status is { } s)
        {
            q = s switch
            {
                ImpersonationGrantStatus.Active =>
                    q.Where(g => !g.RevokedAtUtc.HasValue && !g.EndedAtUtc.HasValue && g.ExpiresAtUtc > now),
                ImpersonationGrantStatus.Ended =>
                    q.Where(g => g.EndedAtUtc.HasValue),
                ImpersonationGrantStatus.Revoked =>
                    q.Where(g => g.RevokedAtUtc.HasValue),
                ImpersonationGrantStatus.Expired =>
                    q.Where(g => !g.RevokedAtUtc.HasValue && !g.EndedAtUtc.HasValue && g.ExpiresAtUtc <= now),
                _ => q,
            };
        }

        var rows = await q
            .OrderByDescending(g => g.StartedAtUtc)
            .Take(Math.Clamp(take, 1, 500))
            .ToListAsync(ct).ConfigureAwait(false);

        return rows.Select(ToDto).ToList();
    }

    // ── helpers ─────────────────────────────────────────────────────────

    /// <summary>
    /// Moves the grant to a terminal state and records <see cref="ImpersonationGrantTerminatedIntegrationEvent"/>
    /// in one transaction, so consumers (the PAF releasing the counter) learn of every termination exactly when
    /// it commits. The row lock serialises a concurrent End and Revoke: the loser re-reads the committed terminal
    /// row and records nothing. The cache is the caller's job, after the commit: a rolled-back termination must
    /// not leave an EndedOrRevoked marker behind.
    /// </summary>
    private async Task<(ImpersonationGrant? Grant, bool Terminated)> TerminateAsync(
        Func<CancellationToken, Task<ImpersonationGrant?>> loadLocked,
        Action<ImpersonationGrant> terminate,
        ImpersonationGrantStatus motivo,
        CancellationToken ct)
    {
        ImpersonationGrant? grant = null;
        var terminated = false;
        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async c =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(c).ConfigureAwait(false);
            try
            {
                grant = await loadLocked(c).ConfigureAwait(false);
                if (grant is null || grant.IsTerminal) return;

                terminate(grant);
                await db.SaveChangesAsync(c).ConfigureAwait(false);
                await outbox.AddAsync(NewTerminatedEvent(grant, motivo), c).ConfigureAwait(false);
                await transaction.CommitAsync(c).ConfigureAwait(false);
                terminated = true;
            }
            finally
            {
                // A failed attempt must not leave the terminal state tracked: a retry would re-read the tracked
                // instance as already terminal, and a caller that swallows the failure (EndImpersonation does)
                // would persist it on its next SaveChanges without the event.
                if (!terminated && grant is not null)
                {
                    db.Entry(grant).State = EntityState.Detached;
                }
            }
        }, ct).ConfigureAwait(false);

        return (grant, terminated);
    }

    private Task<ImpersonationGrant?> LoadLockedByIdAsync(Guid id, CancellationToken ct)
    {
        var query = db.Database.IsNpgsql()
            ? db.ImpersonationGrants.FromSqlRaw(
                $$"""SELECT * FROM "{{IdentityModuleConstants.SchemaName}}"."ImpersonationGrants" WHERE "Id" = {0} FOR UPDATE""",
                id)
            : db.ImpersonationGrants.Where(g => g.Id == id); // no portable row lock; single-writer only

        return query.FirstOrDefaultAsync(ct);
    }

    private Task<ImpersonationGrant?> LoadLockedByJtiAsync(string jti, CancellationToken ct)
    {
        var query = db.Database.IsNpgsql()
            ? db.ImpersonationGrants.FromSqlRaw(
                $$"""SELECT * FROM "{{IdentityModuleConstants.SchemaName}}"."ImpersonationGrants" WHERE "Jti" = {0} FOR UPDATE""",
                jti)
            : db.ImpersonationGrants.Where(g => g.Jti == jti); // no portable row lock; single-writer only

        return query.FirstOrDefaultAsync(ct);
    }

    private ImpersonationGrantTerminatedIntegrationEvent NewTerminatedEvent(ImpersonationGrant grant, ImpersonationGrantStatus motivo) => new(
        Id: Guid.NewGuid(),
        OccurredOnUtc: timeProvider.GetUtcNow().UtcDateTime,
        TenantId: grant.ImpersonatedTenantId,
        CorrelationId: Activity.Current?.Id ?? Guid.NewGuid().ToString(),
        Source: "Identity",
        GrantId: grant.Id,
        Jti: grant.Jti,
        ImpersonatedUserId: grant.ImpersonatedUserId,
        Motivo: motivo);

    private static readonly HybridCacheEntryOptions CacheEntryOptions = new()
    {
        // 5-min TTL suffices: sessions are 60 min max and the entry is re-primed on every revoke/end.
        // After expiry the jti is meaningless anyway (JwtBearer rejects on lifetime).
        Expiration = TimeSpan.FromMinutes(5),
        LocalCacheExpiration = TimeSpan.FromMinutes(1),
        Flags = HybridCacheEntryFlags.DisableCompression,
    };

    private Task SetCachedStatusAsync(ImpersonationGrant grant, GrantState state, CancellationToken ct)
        => cache.SetAsync(
            CacheKeys.ImpersonationGrantStatus(grant.Jti),
            state,
            options: CacheEntryOptions,
            cancellationToken: ct).AsTask();

    private static async ValueTask<GrantState> LoadStateAsync(FactoryState s, CancellationToken ct)
    {
        var row = await s.Db.ImpersonationGrants
            .AsNoTracking()
            .Where(g => g.Jti == s.Jti)
            .Select(g => new { g.EndedAtUtc, g.RevokedAtUtc })
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);

        if (row is null) return GrantState.Unknown;
        if (row.EndedAtUtc.HasValue || row.RevokedAtUtc.HasValue) return GrantState.EndedOrRevoked;
        return GrantState.Active;
    }

    private readonly record struct FactoryState(IdentityDbContext Db, string Jti);

    /// <summary>
    /// Cache-friendly tri-state. Storing an enum (not the full grant) keeps the
    /// cache entry tiny and avoids stale-data issues — the entire row's worth
    /// of info we actually need on the hot path collapses to "is this grant
    /// still good?".
    /// </summary>
    private enum GrantState : byte
    {
        Active = 0,
        EndedOrRevoked = 1,
        /// <summary>No row found for this jti — treat as revoked (defensive).</summary>
        Unknown = 2,
    }

    private ImpersonationGrantDto ToDto(ImpersonationGrant g)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        ImpersonationGrantStatus status;
        if (g.RevokedAtUtc.HasValue)
        {
            status = ImpersonationGrantStatus.Revoked;
        }
        else if (g.EndedAtUtc.HasValue)
        {
            status = ImpersonationGrantStatus.Ended;
        }
        else if (g.ExpiresAtUtc <= now)
        {
            status = ImpersonationGrantStatus.Expired;
        }
        else
        {
            status = ImpersonationGrantStatus.Active;
        }

        return new ImpersonationGrantDto(
            Id: g.Id,
            Jti: g.Jti,
            ActorUserId: g.ActorUserId,
            ActorUserName: g.ActorUserName,
            ActorTenantId: g.ActorTenantId,
            ImpersonatedUserId: g.ImpersonatedUserId,
            ImpersonatedUserName: g.ImpersonatedUserName,
            ImpersonatedTenantId: g.ImpersonatedTenantId,
            Reason: g.Reason,
            StartedAtUtc: g.StartedAtUtc,
            ExpiresAtUtc: g.ExpiresAtUtc,
            EndedAtUtc: g.EndedAtUtc,
            RevokedAtUtc: g.RevokedAtUtc,
            RevokedByUserId: g.RevokedByUserId,
            RevokedByUserName: g.RevokedByUserName,
            RevokeReason: g.RevokeReason,
            Status: status);
    }
}
