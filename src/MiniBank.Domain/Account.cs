namespace MiniBank.Domain;

public sealed class Account
{
    private Account()
    {
        Owner = string.Empty;
        Currency = string.Empty;
    }

    public Guid Id { get; private set; }
    public string Owner { get; private set; }
    public string Currency { get; private set; }
    public long Balance { get; private set; }
    public int Version { get; private set; }
    public AccountStatus Status { get; private set; }
    public bool IsSystem { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    public static Account Open(string? owner, string? currency, DateTimeOffset now, bool isSystem = false, Guid? id = null)
    {
        if (string.IsNullOrWhiteSpace(owner))
            throw new DomainValidationException("Owner must not be empty.");

        return new Account
        {
            Id = id ?? Guid.NewGuid(),
            Owner = owner.Trim(),
            Currency = Currencies.Normalize(currency),
            Balance = 0,
            Version = 0,
            Status = AccountStatus.Active,
            IsSystem = isSystem,
            CreatedAt = now
        };
    }

    public void Block() => Status = AccountStatus.Blocked;

    public LedgerEntry Debit(Guid transferId, Money amount, DateTimeOffset now)
    {
        EnsureOperable(amount);

        long newBalance;
        try
        {
            newBalance = checked(Balance - amount.Amount);
        }
        catch (OverflowException)
        {
            throw new DomainValidationException("Balance overflow.");
        }

        if (newBalance < 0 && !IsSystem)
            throw new InsufficientFundsException($"Account {Id} has insufficient funds.");

        return Apply(transferId, -amount.Amount, newBalance, now);
    }

    public LedgerEntry Credit(Guid transferId, Money amount, DateTimeOffset now)
    {
        EnsureOperable(amount);

        long newBalance;
        try
        {
            newBalance = checked(Balance + amount.Amount);
        }
        catch (OverflowException)
        {
            throw new DomainValidationException("Balance overflow.");
        }

        return Apply(transferId, amount.Amount, newBalance, now);
    }

    private void EnsureOperable(Money amount)
    {
        if (Status == AccountStatus.Blocked)
            throw new AccountBlockedException($"Account {Id} is blocked.");
        if (amount.Currency != Currency)
            throw new DomainValidationException($"Account {Id} holds {Currency}, not {amount.Currency}.");
    }

    private LedgerEntry Apply(Guid transferId, long signedAmount, long newBalance, DateTimeOffset now)
    {
        Balance = newBalance;
        Version++;
        return new LedgerEntry(transferId, Id, signedAmount, newBalance, now);
    }
}
