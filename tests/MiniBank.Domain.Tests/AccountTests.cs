using MiniBank.Domain;

namespace MiniBank.Domain.Tests;

public class AccountTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);

    private static Account Funded(long balance, string currency = "RUB", bool isSystem = false)
    {
        var account = Account.Open("owner", currency, Now, isSystem);
        if (balance > 0)
            account.Credit(Guid.NewGuid(), Money.Of(balance, currency), Now);
        return account;
    }

    [Fact]
    public void Debit_And_Credit_AreNotPublic()
    {
        // проводки создаёт только Transfer
        Assert.Null(typeof(Account).GetMethod("Debit"));
        Assert.Null(typeof(Account).GetMethod("Credit"));
    }

    [Fact]
    public void Open_RemembersUserId_AndDefaultsToNone()
    {
        var userId = Guid.NewGuid();

        Assert.Equal(userId, Account.Open("owner", "RUB", Now, userId: userId).UserId);
        Assert.Null(Account.Open("owner", "RUB", Now).UserId);
    }

    [Fact]
    public void Open_RejectsOwnerLongerThanLimit()
    {
        Assert.Throws<DomainValidationException>(() => Account.Open(new string('x', Account.MaxOwnerLength + 1), "RUB", Now));
        Assert.Equal(Account.MaxOwnerLength, Account.Open(new string('x', Account.MaxOwnerLength), "RUB", Now).Owner.Length);
    }

    [Fact]
    public void Open_RejectsOwnerWithNulCharacter()
    {
        Assert.Throws<DomainValidationException>(() => Account.Open("a\0b", "RUB", Now));
    }

    [Fact]
    public void Open_RejectsEmptyOwner()
    {
        Assert.Throws<DomainValidationException>(() => Account.Open("  ", "RUB", Now));
    }

    [Fact]
    public void Open_NormalizesCurrency()
    {
        var account = Account.Open("a", "rub", Now);

        Assert.Equal("RUB", account.Currency);
        Assert.Equal(0, account.Balance);
        Assert.Equal(AccountStatus.Active, account.Status);
    }

    [Fact]
    public void Debit_ReducesBalanceAndReturnsNegativeEntry()
    {
        var account = Funded(1000);
        var versionBefore = account.Version;
        var transferId = Guid.NewGuid();

        var entry = account.Debit(transferId, Money.Of(500, "RUB"), Now);

        Assert.Equal(500, account.Balance);
        Assert.Equal(-500, entry.Amount);
        Assert.Equal(500, entry.BalanceAfter);
        Assert.Equal(account.Id, entry.AccountId);
        Assert.Equal(transferId, entry.TransferId);
        Assert.Equal(versionBefore + 1, account.Version);
    }

    [Fact]
    public void Debit_BelowZero_Throws()
    {
        var account = Funded(100);
        var versionBefore = account.Version;

        Assert.Throws<InsufficientFundsException>(() => account.Debit(Guid.NewGuid(), Money.Of(101, "RUB"), Now));

        Assert.Equal(100, account.Balance);
        Assert.Equal(versionBefore, account.Version);
    }

    [Fact]
    public void Debit_SystemAccount_MayGoNegative()
    {
        var cash = Funded(0, isSystem: true);

        cash.Debit(Guid.NewGuid(), Money.Of(500, "RUB"), Now);

        Assert.Equal(-500, cash.Balance);
    }

    [Fact]
    public void Credit_IncreasesBalance()
    {
        var account = Funded(0);

        var entry = account.Credit(Guid.NewGuid(), Money.Of(300, "RUB"), Now);

        Assert.Equal(300, account.Balance);
        Assert.Equal(300, entry.Amount);
        Assert.Equal(300, entry.BalanceAfter);
    }

    [Fact]
    public void Blocked_RejectsDebitAndCredit()
    {
        var account = Funded(1000);
        account.Block();

        Assert.Throws<AccountBlockedException>(() => account.Debit(Guid.NewGuid(), Money.Of(1, "RUB"), Now));
        Assert.Throws<AccountBlockedException>(() => account.Credit(Guid.NewGuid(), Money.Of(1, "RUB"), Now));
    }

    [Fact]
    public void Currency_Mismatch_Throws()
    {
        var account = Funded(1000);

        Assert.Throws<DomainValidationException>(() => account.Debit(Guid.NewGuid(), Money.Of(10, "USD"), Now));
        Assert.Throws<DomainValidationException>(() => account.Credit(Guid.NewGuid(), Money.Of(10, "USD"), Now));
    }

    [Fact]
    public void Credit_Overflow_ThrowsValidation()
    {
        var account = Funded(long.MaxValue - 1);
        var versionBefore = account.Version;

        Assert.Throws<DomainValidationException>(() => account.Credit(Guid.NewGuid(), Money.Of(2, "RUB"), Now));

        Assert.Equal(long.MaxValue - 1, account.Balance);
        Assert.Equal(versionBefore, account.Version);
    }
}
