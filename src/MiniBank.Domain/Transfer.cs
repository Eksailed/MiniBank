namespace MiniBank.Domain;

public sealed class Transfer
{
    private readonly List<LedgerEntry> _entries = [];

    private Transfer()
    {
        Currency = string.Empty;
    }

    public Guid Id { get; private set; }
    public Guid FromAccountId { get; private set; }
    public Guid ToAccountId { get; private set; }
    public long Amount { get; private set; }
    public string Currency { get; private set; }
    public TransferStatus Status { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public IReadOnlyList<LedgerEntry> Entries => _entries;

    public static Transfer Execute(Account from, Account to, Money amount, DateTimeOffset now)
    {
        if (from.Id == to.Id)
            throw new DomainValidationException("Cannot transfer to the same account.");
        if (from.Currency != to.Currency || from.Currency != amount.Currency)
            throw new DomainValidationException("Transfer requires accounts and amount in the same currency.");
        if (from.Status == AccountStatus.Blocked)
            throw new AccountBlockedException($"Account {from.Id} is blocked.");
        if (to.Status == AccountStatus.Blocked)
            throw new AccountBlockedException($"Account {to.Id} is blocked.");
        if (to.Balance > long.MaxValue - amount.Amount)
            throw new DomainValidationException("Balance overflow.");

        var transfer = new Transfer
        {
            Id = Guid.NewGuid(),
            FromAccountId = from.Id,
            ToAccountId = to.Id,
            Amount = amount.Amount,
            Currency = amount.Currency,
            Status = TransferStatus.Completed,
            CreatedAt = now
        };

        // Дебет первым: единственная проверка, которая ещё может отказать, — достаточность средств.
        transfer._entries.Add(from.Debit(transfer.Id, amount, now));
        transfer._entries.Add(to.Credit(transfer.Id, amount, now));
        return transfer;
    }
}
