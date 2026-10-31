using System.Security.Cryptography;
using System.Text;
using MiniBank.Application.Abstractions;
using MiniBank.Domain;

namespace MiniBank.Application;

public sealed class TransfersService(
    IAccountRepository accounts,
    ITransferRepository transfers,
    IUnitOfWork uow,
    IdempotencyExecutor idempotency,
    TimeProvider clock)
{
    public async Task<IdempotentResult<TransferDto>> TransferAsync(
        string? idempotencyKey, Guid fromId, Guid toId, long amount, string? currency, CancellationToken ct)
    {
        if (fromId == Guid.Empty || toId == Guid.Empty)
            throw new DomainValidationException("Both fromAccountId and toAccountId are required.");

        var money = Money.Of(amount, currency);
        var hash = Hash($"transfer|{fromId}|{toId}|{money.Amount}|{money.Currency}");

        return await idempotency.ExecuteAsync(idempotencyKey, hash, async token =>
        {
            var locked = await accounts.FindForUpdateAsync([fromId, toId], token);
            var from = locked.GetValueOrDefault(fromId) ?? throw new AccountNotFoundException(fromId);
            var to = locked.GetValueOrDefault(toId) ?? throw new AccountNotFoundException(toId);
            if (from.IsSystem || to.IsSystem)
                throw new TransferNotAllowedException("System accounts cannot take part in client transfers.");

            return await ExecuteAsync(from, to, money, token);
        }, ct);
    }

    public async Task<IdempotentResult<TransferDto>> DepositAsync(
        string? idempotencyKey, Guid accountId, long amount, string? currency, CancellationToken ct)
    {
        var money = Money.Of(amount, currency);
        var cashId = SystemAccounts.CashId(money.Currency);
        var hash = Hash($"deposit|{accountId}|{money.Amount}|{money.Currency}");

        return await idempotency.ExecuteAsync(idempotencyKey, hash, async token =>
        {
            var locked = await accounts.FindForUpdateAsync([accountId, cashId], token);
            var account = locked.GetValueOrDefault(accountId) ?? throw new AccountNotFoundException(accountId);
            if (account.IsSystem)
                throw new TransferNotAllowedException("System accounts cannot be deposited to.");
            var cash = locked.GetValueOrDefault(cashId) ?? throw new AccountNotFoundException(cashId);

            return await ExecuteAsync(cash, account, money, token);
        }, ct);
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
