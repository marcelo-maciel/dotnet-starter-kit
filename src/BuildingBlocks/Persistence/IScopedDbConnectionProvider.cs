using System.Data.Common;

namespace FSH.Framework.Persistence;

/// <summary>
/// One <see cref="DbConnection"/> per connection string per DI scope, shared by every DbContext in
/// that scope.
///
/// Required for cross-context transactions: EF Core can enlist a second context in an existing
/// transaction only when both contexts use the same connection instance, and Npgsql has no
/// distributed-transaction promotion to fall back on. Without this, the outbox write is always a
/// separate transaction from the business write it is supposed to accompany.
/// </summary>
public interface IScopedDbConnectionProvider
{
    /// <summary>
    /// Returns the scope's connection for <paramref name="connectionString"/>, creating it on
    /// first use. The connection is owned by the scope, not by any DbContext.
    /// </summary>
    DbConnection GetConnection(string dbProvider, string connectionString);
}
