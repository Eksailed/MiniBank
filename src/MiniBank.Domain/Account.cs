namespace MiniBank.Domain;

public sealed class Account
{
    public const int MaxOwnerLength = 200;

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

    /// <summary>Владелец счёта; <c>null</c> у кассы и у счетов, открытых до появления пользователей.</summary>
    public Guid? UserId { get; private set; }

    /// <summary>Лимит одного исходящего перевода; <c>null</c> у кассы и у счетов, открытых до появления лимитов.</summary>
    public long? PerOperationLimit { get; private set; }

    /// <summary>Лимит исходящих переводов за сутки UTC; <c>null</c> там же, где и <see cref="PerOperationLimit"/>.</summary>
    public long? DailyLimit { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    public static Account Open(string? owner, string? currency, DateTimeOffset now, bool isSystem = false, Guid? id = null, Guid? userId = null,
        AccountLimits? limits = null)
    {
        if (string.IsNullOrWhiteSpace(owner))
            throw new DomainValidationException("Owner must not be empty.");
        if (owner.Trim().Length > MaxOwnerLength)
            throw new DomainValidationException($"Owner must be at most {MaxOwnerLength} characters.");
        if (owner.Contains('\0'))
            throw new DomainValidationException("Owner must not contain control characters.");
        if (limits is null && !isSystem)
            throw new DomainValidationException("Limits are required for a client account.");

        return new Account
        {
            Id = id ?? Guid.NewGuid(),
            Owner = owner.Trim(),
            Currency = Currencies.Normalize(currency),
            Balance = 0,
            Version = 0,
            Status = AccountStatus.Active,
            IsSystem = isSystem,
            UserId = userId,
            PerOperationLimit = limits?.PerOperation,
            DailyLimit = limits?.Daily,
            CreatedAt = now
        };
    }

    public void Block() => Status = AccountStatus.Blocked;

    public void SetLimits(AccountLimits limits)
    {
        PerOperationLimit = limits.PerOperation;
        DailyLimit = limits.Daily;
    }

    /// <summary>
    /// Проверяет исходящий перевод на лимиты; у касс и у счетов без лимитов проверка не выполняется.
    /// Если превышены оба, сообщается лимит одной операции.
    /// </summary>
    /// <param name="spentToday">Уже потрачено с начала текущих суток UTC (сумма модулей исходящих проводок).</param>
    public void EnsureCanSpend(long amount, long spentToday)
    {
        if (IsSystem || PerOperationLimit is not { } perOperation || DailyLimit is not { } daily)
            return;

        if (amount > perOperation)
            throw new PerOperationLimitExceededException(perOperation);

        var remaining = Math.Max(0, daily - spentToday);
        if (amount > remaining)
            throw new DailyLimitExceededException(daily, remaining);
    }

    internal LedgerEntry Debit(Guid transferId, Money amount, DateTimeOffset now)
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

    internal LedgerEntry Credit(Guid transferId, Money amount, DateTimeOffset now)
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
