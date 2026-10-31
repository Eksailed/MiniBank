using MiniBank.Application.Abstractions;
using MiniBank.Domain;

namespace MiniBank.Application;

public sealed class TransfersService(
    IAccountRepository accounts, ITransferRepository transfers, IUnitOfWork uow, TimeProvider clock)
{
    public async Task<TransferDto> TransferAsync(
        Guid fromId, Guid toId, long amount, string? currency, CancellationToken ct)
    {
        var money = Money.Of(amount, currency);

        var from = await accounts.FindAsync(fromId, ct) ?? throw new AccountNotFoundException(fromId);
        var to = await accounts.FindAsync(toId, ct) ?? throw new AccountNotFoundException(toId);
        if (from.IsSystem || to.IsSystem)
            throw new TransferNotAllowedException("System accounts cannot take part in client transfers.");

        return await ExecuteAsync(from, to, money, ct);
    }

    public async Task<TransferDto> DepositAsync(Guid accountId, long amount, string? currency, CancellationToken ct)
    {
        var money = Money.Of(amount, currency);

        var account = await accounts.FindAsync(accountId, ct) ?? throw new AccountNotFoundException(accountId);
        if (account.IsSystem)
            throw new TransferNotAllowedException("System accounts cannot be deposited to.");

        var cashId = SystemAccounts.CashId(money.Currency);
        var cash = await accounts.FindAsync(cashId, ct) ?? throw new AccountNotFoundException(cashId);

        return await ExecuteAsync(cash, account, money, ct);
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
