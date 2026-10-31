using MiniBank.Application.Abstractions;
using MiniBank.Domain;

namespace MiniBank.Application;

public sealed class TransfersService(
    IAccountRepository accounts, ITransferRepository transfers, IUnitOfWork uow, TimeProvider clock)
{
    public async Task<TransferDto> TransferAsync(
        Guid fromId, Guid toId, long amount, string? currency, CancellationToken ct)
    {
        if (fromId == Guid.Empty || toId == Guid.Empty)
            throw new DomainValidationException("Both fromAccountId and toAccountId are required.");

        var money = Money.Of(amount, currency);

        return await uow.ExecuteInTransactionAsync(async token =>
        {
            var locked = await accounts.FindForUpdateAsync([fromId, toId], token);
            var from = locked.GetValueOrDefault(fromId) ?? throw new AccountNotFoundException(fromId);
            var to = locked.GetValueOrDefault(toId) ?? throw new AccountNotFoundException(toId);
            if (from.IsSystem || to.IsSystem)
                throw new TransferNotAllowedException("System accounts cannot take part in client transfers.");

            return await ExecuteAsync(from, to, money, token);
        }, ct);
    }

    public async Task<TransferDto> DepositAsync(Guid accountId, long amount, string? currency, CancellationToken ct)
    {
        var money = Money.Of(amount, currency);
        var cashId = SystemAccounts.CashId(money.Currency);

        return await uow.ExecuteInTransactionAsync(async token =>
        {
            var locked = await accounts.FindForUpdateAsync([accountId, cashId], token);
            var account = locked.GetValueOrDefault(accountId) ?? throw new AccountNotFoundException(accountId);
            if (account.IsSystem)
                throw new TransferNotAllowedException("System accounts cannot be deposited to.");
            var cash = locked.GetValueOrDefault(cashId) ?? throw new AccountNotFoundException(cashId);

            return await ExecuteAsync(cash, account, money, token);
        }, ct);
    }

    private async Task<TransferDto> ExecuteAsync(Account from, Account to, Money money, CancellationToken ct)
    {
        var transfer = Transfer.Execute(from, to, money, clock.GetUtcNow());
        transfers.Add(transfer);
        await uow.SaveChangesAsync(ct);
        return new TransferDto(
            transfer.Id, transfer.FromAccountId, transfer.ToAccountId, transfer.Amount, transfer.Currency, transfer.CreatedAt);
    }
}
