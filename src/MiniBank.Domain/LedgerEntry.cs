namespace MiniBank.Domain;

/// <summary>Проводка по счёту. Только добавляется, не меняется. Сумма знаковая: списание отрицательное.</summary>
public sealed class LedgerEntry
{
    private LedgerEntry()
    {
    }

    internal LedgerEntry(Guid transferId, Guid accountId, long amount, long balanceAfter, DateTimeOffset createdAt)
    {
        Id = Guid.NewGuid();
        TransferId = transferId;
        AccountId = accountId;
        Amount = amount;
        BalanceAfter = balanceAfter;
        CreatedAt = createdAt;
    }

    public Guid Id { get; private set; }
    public Guid TransferId { get; private set; }
    public Guid AccountId { get; private set; }
    public long Amount { get; private set; }
    public long BalanceAfter { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
}
