using MiniBank.Application.Abstractions;
using MiniBank.Domain;

namespace MiniBank.Application;

public sealed class AccountsService(
    IAccountRepository accounts, IUserRepository users, ILedgerQuery ledger, IUnitOfWork uow, TimeProvider clock)
{
    public const int MaxAccountsPerUser = 50;

    private const int MaxPageSize = 100;

    /// <summary>
    /// Открывает счёт; владелец — пользователь, имя владельца — его отображаемое имя. Строка пользователя блокируется
    /// на время проверки лимита, поэтому параллельные запросы не создают больше <see cref="MaxAccountsPerUser"/> счетов.
    /// </summary>
    public async Task<AccountDto> CreateAsync(Guid userId, string? currency, CancellationToken ct) =>
        await uow.ExecuteInTransactionAsync(async token =>
        {
            var user = await users.FindByIdForUpdateAsync(userId, token) ?? throw new InvalidCredentialsException();
            if (await accounts.CountByUserAsync(userId, token) >= MaxAccountsPerUser)
                throw new AccountLimitReachedException(MaxAccountsPerUser);

            var account = Account.Open(user.DisplayName, currency, clock.GetUtcNow(), userId: userId);
            accounts.Add(account);
            await uow.SaveChangesAsync(token);
            return ToDto(account);
        }, ct);

    public async Task<IReadOnlyList<AccountDto>> ListAsync(Guid userId, CancellationToken ct) =>
        (await accounts.ListByUserAsync(userId, ct)).Select(ToDto).ToList();

    public async Task<AccountDto> GetAsync(Guid userId, Guid id, CancellationToken ct) =>
        ToDto(await FindOwnedAsync(userId, id, ct));

    public async Task<IReadOnlyList<EntryDto>> GetEntriesAsync(
        Guid userId, Guid id, int page, int pageSize, CancellationToken ct)
    {
        if (page < 1)
            throw new DomainValidationException("Page must be at least 1.");
        if (pageSize is < 1 or > MaxPageSize)
            throw new DomainValidationException($"Page size must be between 1 and {MaxPageSize}.");

        if ((long)(page - 1) * pageSize > int.MaxValue)
            throw new DomainValidationException("Page is out of range.");

        await FindOwnedAsync(userId, id, ct);

        var entries = await ledger.GetEntriesAsync(id, page, pageSize, ct);
        return entries
            .Select(e => new EntryDto(e.Id, e.TransferId, e.Amount, e.BalanceAfter, e.CreatedAt))
            .ToList();
    }

    /// <summary>Чужой, системный и «ничейный» счета неотличимы от несуществующего: везде 404.</summary>
    private async Task<Account> FindOwnedAsync(Guid userId, Guid id, CancellationToken ct)
    {
        var account = await accounts.FindAsync(id, ct);
        return account is { UserId: { } owner } && owner == userId ? account : throw new AccountNotFoundException(id);
    }

    private static AccountDto ToDto(Account a) =>
        new(a.Id, a.Owner, a.Currency, a.Balance, a.Status.ToString(), a.IsSystem, a.CreatedAt);
}
