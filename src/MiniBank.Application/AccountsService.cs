using MiniBank.Application.Abstractions;
using MiniBank.Domain;

namespace MiniBank.Application;

public sealed class AccountsService(IAccountRepository accounts, ILedgerQuery ledger, IUnitOfWork uow, TimeProvider clock)
{
    private const int MaxPageSize = 100;

    public async Task<AccountDto> CreateAsync(string? owner, string? currency, CancellationToken ct)
    {
        var account = Account.Open(owner, currency, clock.GetUtcNow());
        accounts.Add(account);
        await uow.SaveChangesAsync(ct);
        return ToDto(account);
    }

    public async Task<AccountDto> GetAsync(Guid id, CancellationToken ct) =>
        ToDto(await accounts.FindAsync(id, ct) ?? throw new AccountNotFoundException(id));

    public async Task<IReadOnlyList<EntryDto>> GetEntriesAsync(Guid id, int page, int pageSize, CancellationToken ct)
    {
        if (page < 1)
            throw new DomainValidationException("Page must be at least 1.");
        if (pageSize is < 1 or > MaxPageSize)
            throw new DomainValidationException($"Page size must be between 1 and {MaxPageSize}.");

        if ((long)(page - 1) * pageSize > int.MaxValue)
            throw new DomainValidationException("Page is out of range.");

        if (await accounts.FindAsync(id, ct) is null)
            throw new AccountNotFoundException(id);

        var entries = await ledger.GetEntriesAsync(id, page, pageSize, ct);
        return entries
            .Select(e => new EntryDto(e.Id, e.TransferId, e.Amount, e.BalanceAfter, e.CreatedAt))
            .ToList();
    }

    private static AccountDto ToDto(Account a) =>
        new(a.Id, a.Owner, a.Currency, a.Balance, a.Status.ToString(), a.IsSystem, a.CreatedAt);
}
