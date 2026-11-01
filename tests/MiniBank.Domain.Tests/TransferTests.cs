using MiniBank.Domain;

namespace MiniBank.Domain.Tests;

public class TransferTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);

    private static Account Funded(long balance, string currency = "RUB")
    {
        var account = Account.Open("owner", currency, Now, limits: new AccountLimits(1_000_000, 3_000_000));
        if (balance > 0)
            account.Credit(Guid.NewGuid(), Money.Of(balance, currency), Now);
        return account;
    }

    [Theory]
    [InlineData(1)]
    [InlineData(500)]
    [InlineData(99999)]
    public void Execute_CreatesTwoEntriesSummingToZero(long amount)
    {
        var transfer = Transfer.Execute(Funded(100000), Funded(0), Money.Of(amount, "RUB"), Now);

        Assert.Equal(2, transfer.Entries.Count);
        Assert.Equal(0, transfer.Entries.Sum(e => e.Amount));
    }

    [Fact]
    public void Execute_MovesMoney()
    {
        var from = Funded(1000);
        var to = Funded(0);

        Transfer.Execute(from, to, Money.Of(300, "RUB"), Now);

        Assert.Equal(700, from.Balance);
        Assert.Equal(300, to.Balance);
    }

    [Fact]
    public void Execute_SameAccount_Throws()
    {
        var account = Funded(1000);

        Assert.Throws<DomainValidationException>(() => Transfer.Execute(account, account, Money.Of(1, "RUB"), Now));
    }

    [Fact]
    public void Execute_InsufficientFunds_ChangesNothing()
    {
        var from = Funded(100);
        var to = Funded(0);

        Assert.Throws<InsufficientFundsException>(() => Transfer.Execute(from, to, Money.Of(200, "RUB"), Now));

        Assert.Equal(100, from.Balance);
        Assert.Equal(0, to.Balance);
    }

    [Fact]
    public void Execute_CurrencyMismatch_Throws()
    {
        var from = Funded(1000, "RUB");
        var to = Funded(0, "USD");

        Assert.Throws<DomainValidationException>(() => Transfer.Execute(from, to, Money.Of(10, "RUB"), Now));

        Assert.Equal(1000, from.Balance);
        Assert.Equal(0, to.Balance);
    }

    [Fact]
    public void Execute_BlockedReceiver_ChangesNothing()
    {
        var from = Funded(1000);
        var to = Funded(0);
        to.Block();

        Assert.Throws<AccountBlockedException>(() => Transfer.Execute(from, to, Money.Of(10, "RUB"), Now));

        Assert.Equal(1000, from.Balance);
    }

    [Fact]
    public void Execute_RecordsFromToAndStatus()
    {
        var from = Funded(1000);
        var to = Funded(0);

        var transfer = Transfer.Execute(from, to, Money.Of(250, "RUB"), Now);

        Assert.Equal(TransferStatus.Completed, transfer.Status);
        Assert.Equal(from.Id, transfer.FromAccountId);
        Assert.Equal(to.Id, transfer.ToAccountId);
        Assert.Equal(250, transfer.Amount);
        Assert.Equal("RUB", transfer.Currency);
        Assert.All(transfer.Entries, e => Assert.Equal(transfer.Id, e.TransferId));
    }

    [Fact]
    public void CashId_IsStableAndRejectsUnknown()
    {
        Assert.Equal(Guid.Parse("00000000-0000-0000-0000-0000000000a1"), SystemAccounts.CashId("RUB"));
        Assert.Throws<DomainValidationException>(() => SystemAccounts.CashId("JPY"));
        Assert.True(SystemAccounts.IsCashId(SystemAccounts.CashId("EUR")));
        Assert.False(SystemAccounts.IsCashId(Guid.NewGuid()));
    }
}
