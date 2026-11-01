using System.Security.Cryptography;
using System.Text;
using MiniBank.Application.Abstractions;
using MiniBank.Application.Limits;
using MiniBank.Domain;

namespace MiniBank.Application;

public sealed class TransfersService(
    IAccountRepository accounts,
    ITransferRepository transfers,
    IUnitOfWork uow,
    ILedgerQuery ledger,
    IdempotencyExecutor idempotency,
    TimeProvider clock)
{
    public async Task<IdempotentResult<TransferDto>> TransferAsync(
        Guid userId, string? idempotencyKey, Guid fromId, Guid toId, long amount, string? currency, CancellationToken ct)
    {
        if (fromId == Guid.Empty || toId == Guid.Empty)
            throw new DomainValidationException("Both fromAccountId and toAccountId are required.");

        var money = Money.Of(amount, currency);
        var hash = Hash($"transfer|{userId}|{fromId}|{toId}|{money.Amount}|{money.Currency}");

        // До обращения к идемпотентности: чужой ключ с чужим счётом не должен ни вернуть сохранённый ответ, ни выдать факт его наличия.
        await EnsureOwnedAsync(userId, fromId, ct);

        return await idempotency.ExecuteAsync(userId, idempotencyKey, hash, async token =>
        {
            var locked = await accounts.FindForUpdateAsync([fromId, toId], token);
            var from = locked.GetValueOrDefault(fromId) ?? throw new AccountNotFoundException(fromId);
            if (from.UserId != userId)
                throw new AccountNotFoundException(fromId);
            var to = locked.GetValueOrDefault(toId) ?? throw new AccountNotFoundException(toId);
            if (from.IsSystem || to.IsSystem)
                throw new TransferNotAllowedException("System accounts cannot take part in client transfers.");

            // Расход читается после блокировки счёта: параллельные переводы с него идут по очереди и видят друг друга.
            var spentToday = await ledger.GetSpentSinceAsync(fromId, LimitPolicy.StartOfDay(clock.GetUtcNow()), token);
            from.EnsureCanSpend(money.Amount, spentToday);

            return await ExecuteAsync(from, to, money, token);
        }, ct);
    }

    public async Task<IdempotentResult<TransferDto>> DepositAsync(
        Guid userId, string? idempotencyKey, Guid accountId, long amount, string? currency, CancellationToken ct)
    {
        var money = Money.Of(amount, currency);
        var cashId = SystemAccounts.CashId(money.Currency);
        var hash = Hash($"deposit|{userId}|{accountId}|{money.Amount}|{money.Currency}");

        await EnsureOwnedAsync(userId, accountId, ct);

        return await idempotency.ExecuteAsync(userId, idempotencyKey, hash, async token =>
        {
            var locked = await accounts.FindForUpdateAsync([accountId, cashId], token);
            var account = locked.GetValueOrDefault(accountId) ?? throw new AccountNotFoundException(accountId);
            if (account.UserId != userId)
                throw new AccountNotFoundException(accountId);
            if (account.IsSystem)
                throw new TransferNotAllowedException("System accounts cannot be deposited to.");
            var cash = locked.GetValueOrDefault(cashId) ?? throw new AccountNotFoundException(cashId);

            return await ExecuteAsync(cash, account, money, token);
        }, ct);
    }

    /// <summary>Чужой, системный и «ничейный» счета неотличимы от несуществующего: 404.</summary>
    private async Task EnsureOwnedAsync(Guid userId, Guid accountId, CancellationToken ct)
    {
        if (!await accounts.IsOwnedByAsync(accountId, userId, ct))
            throw new AccountNotFoundException(accountId);
    }

    private static string Hash(string canonicalRequest) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalRequest)));

    private async Task<TransferDto> ExecuteAsync(Account from, Account to, Money money, CancellationToken ct)
    {
        var transfer = Transfer.Execute(from, to, money, clock.GetUtcNow());
        transfers.Add(transfer);
        await uow.SaveChangesAsync(ct);
        return new TransferDto(
            transfer.Id, transfer.FromAccountId, transfer.ToAccountId, transfer.Amount, transfer.Currency, transfer.CreatedAt);
    }
}
