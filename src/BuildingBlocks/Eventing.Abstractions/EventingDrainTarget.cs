namespace FSH.Framework.Eventing.Abstractions;

/// <summary>
/// One database the outbox dispatcher must drain.
///
/// A null <paramref name="ConnectionString"/> means the default configured database, which holds
/// the outbox for every tenant that does not have a dedicated one. Tenants sharing the default
/// database need no separate target: outbox rows are <c>IGlobalEntity</c>, so a single pass over
/// that database sees all of them regardless of which tenant wrote them.
/// </summary>
/// <param name="TenantId">Tenant whose context is installed while draining, or null for the default pass.</param>
/// <param name="ConnectionString">Dedicated connection string, or null for the default.</param>
public sealed record EventingDrainTarget(string? TenantId, string? ConnectionString);
