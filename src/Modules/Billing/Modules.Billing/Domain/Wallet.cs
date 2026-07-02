using FSH.Framework.Core.Domain;
using FSH.Modules.Billing.Contracts;

namespace FSH.Modules.Billing.Domain;

public sealed class Wallet : AggregateRoot<Guid>
{
    private readonly List<WalletTransaction> _transactions = new();

    public string TenantId { get; private set; } = default!;
    public Money Balance { get; private set; } = default!;
    public string Currency => Balance.Currency;
    public WalletStatus Status { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public DateTime? UpdatedAtUtc { get; private set; }

    public IReadOnlyList<WalletTransaction> Transactions => _transactions;

    private Wallet() { }

    public static Wallet Create(string tenantId, string currency)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        return new Wallet
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            Balance = Money.Zero(string.IsNullOrWhiteSpace(currency) ? "USD" : currency),
            Status = WalletStatus.Active,
            CreatedAtUtc = DateTime.UtcNow
        };
    }

    public WalletTransaction Credit(Money amount, WalletTransactionKind kind, string description, string? referenceId)
    {
        ArgumentNullException.ThrowIfNull(amount);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(amount.Amount, 0m);
        var tx = WalletTransaction.Create(Id, TenantId, amount, kind, description, referenceId);
        _transactions.Add(tx);
        Balance = Balance.Add(amount);
        UpdatedAtUtc = DateTime.UtcNow;
        return tx;
    }

    public WalletTransaction Debit(Money amount, WalletTransactionKind kind, string description, string? referenceId)
    {
        ArgumentNullException.ThrowIfNull(amount);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(amount.Amount, 0m);
        var remaining = Balance.Subtract(amount);
        if (remaining.Amount < 0m)
            throw new InvalidOperationException("Insufficient wallet balance.");
        var tx = WalletTransaction.Create(Id, TenantId, amount with { Amount = -amount.Amount }, kind, description, referenceId);
        _transactions.Add(tx);
        Balance = remaining;
        UpdatedAtUtc = DateTime.UtcNow;
        return tx;
    }
}
