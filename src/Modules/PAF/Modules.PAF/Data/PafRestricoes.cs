using Npgsql;

namespace FSH.Modules.PAF.Data;

/// <summary>Reads which PostgreSQL constraint refused a write: each one has its own answer (retry, refuse, return stored).</summary>
internal static class PafRestricoes
{
    /// <summary>EF wraps the PostgresException of SaveChanges; a raw SqlQuery throws it bare. Both are checked.</summary>
    public static bool Violou(Exception ex, string restricao) =>
        (ex as PostgresException ?? ex.InnerException as PostgresException) is
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
        } pg && pg.ConstraintName == restricao;

    /// <summary>A deadlock aborts the transaction like a unique violation and gets the same answer: a new one.</summary>
    public static bool Impasse(Exception ex) =>
        (ex as PostgresException ?? ex.InnerException as PostgresException) is { SqlState: PostgresErrorCodes.DeadlockDetected };
}
