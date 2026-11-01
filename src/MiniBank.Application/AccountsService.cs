using MiniBank.Application.Abstractions;
using MiniBank.Application.Limits;
using MiniBank.Domain;

namespace MiniBank.Application;

public sealed class AccountsService(
    IAccountRepository accounts,
    IUserRepository users,
    ILedgerQuery ledger,
    IUnitOfWork uow,
    LimitPolicy limitPolicy,
    TimeProvider clock)
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

            var account = Account.Open(
                user.DisplayName, currency, clock.GetUtcNow(), userId: userId, limits: limitPolicy.Default(currency!));
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

    public async Task<LimitsDto> GetLimitsAsync(Guid userId, Guid id, CancellationToken ct) =>
        await ToLimitsDtoAsync(await FindOwnedAsync(userId, id, ct), ct);

    /// <summary>Меняет лимиты своего счёта; значения выше потолка валюты отвергаются.</summary>
    public async Task<LimitsDto> SetLimitsAsync(Guid userId, Guid id, long perOperation, long daily, CancellationToken ct)
    {
        var owned = await FindOwnedAsync(userId, id, ct);
        var limits = new AccountLimits(perOperation, daily);
        limitPolicy.EnsureWithinMax(owned.Currency, limits);

        // Строка блокируется, чтобы смена лимитов не конфликтовала с идущим переводом по оптимистичной версии.
        await uow.ExecuteInTransactionAsync(async token =>
        {
            var locked = (await accounts.FindForUpdateAsync([id], token)).GetValueOrDefault(id)
                ?? throw new AccountNotFoundException(id);
            locked.SetLimits(limits);
            await uow.SaveChangesAsync(token);
            return 0;
        }, ct);

        return await GetLimitsAsync(userId, id, ct);
    }

    /// <summary>Чужой, системный и «ничейный» счета неотличимы от несуществующего: везде 404.</summary>
    private async Task<Account> FindOwnedAsync(Guid userId, Guid id, CancellationToken ct)
    {
        var account = await accounts.FindAsync(id, ct);
        return account is { UserId: { } owner } && owner == userId ? account : throw new AccountNotFoundException(id);
    }

    private async Task<LimitsDto> ToLimitsDtoAsync(Account account, CancellationToken ct)
    {
        var perOperation = account.PerOperationLimit
            ?? throw new InvalidOperationException($"Account {account.Id} has no limits.");
        var daily = account.DailyLimit ?? throw new InvalidOperationException($"Account {account.Id} has no limits.");
        var spent = await ledger.GetSpentSinceAsync(account.Id, LimitPolicy.StartOfDay(clock.GetUtcNow()), ct);
        var max = limitPolicy.Max(account.Currency);
        return new LimitsDto(perOperation, daily, spent, Math.Max(0, daily - spent), max.PerOperation, max.Daily);
    }

    private static AccountDto ToDto(Account a) =>
        new(a.Id, a.Owner, a.Currency, a.Balance, a.Status.ToString(), a.IsSystem, a.CreatedAt);
}
