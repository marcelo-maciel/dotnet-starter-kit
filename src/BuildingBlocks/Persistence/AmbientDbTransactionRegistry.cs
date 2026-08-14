using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace FSH.Framework.Persistence;

/// <summary>
/// Tracks the open transaction on each connection in the current DI scope.
///
/// <see cref="DbConnection"/> exposes no way to ask "is a transaction open on you?", so a context
/// that wants to join another context's transaction has no way to find it. This interceptor is
/// attached to every Hero DbContext and records transactions as they start and end, which is what
/// lets the outbox write enlist in the business transaction instead of committing separately.
/// </summary>
public sealed class AmbientDbTransactionRegistry : IDbTransactionInterceptor
{
    private readonly Dictionary<DbConnection, DbTransaction> _open = [];

    /// <summary>
    /// The transaction currently open on <paramref name="connection"/>, or null when there is
    /// none — in which case a write simply commits on its own, as it always has.
    /// </summary>
    public DbTransaction? Find(DbConnection connection)
        => connection is not null && _open.TryGetValue(connection, out var transaction) ? transaction : null;

    public void TransactionStarted(DbConnection connection, TransactionEndEventData eventData)
        => Track(connection, eventData?.Transaction);

    public void TransactionUsed(DbConnection connection, TransactionEventData eventData)
        => Track(connection, eventData?.Transaction);

    public void TransactionCommitted(DbTransaction transaction, TransactionEndEventData eventData)
        => Forget(transaction);

    public void TransactionRolledBack(DbTransaction transaction, TransactionEndEventData eventData)
        => Forget(transaction);

    public void TransactionFailed(DbTransaction transaction, TransactionErrorEventData eventData)
        => Forget(transaction);

    private void Track(DbConnection connection, DbTransaction? transaction)
    {
        if (connection is not null && transaction is not null)
        {
            _open[connection] = transaction;
        }
    }

    private void Forget(DbTransaction? transaction)
    {
        if (transaction?.Connection is not null)
        {
            _open.Remove(transaction.Connection);
        }
    }
}
