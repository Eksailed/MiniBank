using MiniBank.Application.Statements;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MiniBank.Application;
using MiniBank.Domain;
using MiniBank.Infrastructure.Persistence;
using MiniBank.TestSupport;

namespace MiniBank.Infrastructure.Tests;

[Collection("postgres")]
public class ServicesTests(PostgresFixture postgres)
{
    private async Task<ServiceProvider> MigratedProviderAsync()
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        var provider = new ServiceCollection()
            .AddInfrastructure(connectionString)
            .AddApplication()
            .BuildServiceProvider();

        await using var scope = provider.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<MiniBankDbContext>().Database.MigrateAsync();
        return provider;
    }

    private static async Task<T> InScopeAsync<T>(ServiceProvider provider, Func<IServiceProvider, Task<T>> action)
    {
        await using var scope = provider.CreateAsyncScope();
        return await action(scope.ServiceProvider);
    }

    private static Task<Guid> NewUserAsync(ServiceProvider provider) =>
        InScopeAsync(provider, async sp => (await TestData.SeedUserAsync(sp.GetRequiredService<MiniBankDbContext>())).Id);

    private static Task<AccountDto> CreateAsync(ServiceProvider provider, Guid userId, string currency = "RUB") =>
        InScopeAsync(provider, sp => sp.GetRequiredService<AccountsService>().CreateAsync(userId, currency, default));

    private static Task<IdempotentResult<TransferDto>> DepositWithKeyAsync(
        ServiceProvider provider, Guid userId, string? key, Guid id, long amount, string currency = "RUB") =>
        InScopeAsync(provider, sp => sp.GetRequiredService<TransfersService>().DepositAsync(userId, key, id, amount, currency, default));

    private static Task<IdempotentResult<TransferDto>> TransferWithKeyAsync(
        ServiceProvider provider, Guid userId, string? key, Guid from, Guid to, long amount, string currency = "RUB") =>
        InScopeAsync(provider, sp => sp.GetRequiredService<TransfersService>().TransferAsync(userId, key, from, to, amount, currency, default));

    private static async Task<TransferDto> DepositAsync(ServiceProvider provider, Guid userId, Guid id, long amount, string currency = "RUB") =>
        (await DepositWithKeyAsync(provider, userId, Guid.NewGuid().ToString(), id, amount, currency)).Value;

    private static async Task<TransferDto> TransferAsync(ServiceProvider provider, Guid userId, Guid from, Guid to, long amount, string currency = "RUB") =>
        (await TransferWithKeyAsync(provider, userId, Guid.NewGuid().ToString(), from, to, amount, currency)).Value;

    private static Task<AccountDto> GetAsync(ServiceProvider provider, Guid userId, Guid id) =>
        InScopeAsync(provider, sp => sp.GetRequiredService<AccountsService>().GetAsync(userId, id, default));

    private static Task<IReadOnlyList<EntryDto>> EntriesAsync(
        ServiceProvider provider, Guid userId, Guid id, int page = 1, int pageSize = 50) =>
        InScopeAsync(provider, sp => sp.GetRequiredService<AccountsService>().GetEntriesAsync(userId, id, EntryFilter.None, page, pageSize, default));

    private static Task<long> CashBalanceAsync(ServiceProvider provider, string currency = "RUB") =>
        InScopeAsync(provider, sp => sp.GetRequiredService<MiniBankDbContext>().Accounts
            .Where(a => a.Id == SystemAccounts.CashId(currency)).Select(a => a.Balance).SingleAsync());

    private static Task<(long Sum, int Entries)> TotalsAsync(ServiceProvider provider) =>
        InScopeAsync(provider, async sp =>
        {
            var db = sp.GetRequiredService<MiniBankDbContext>();
            return (await db.Accounts.SumAsync(a => a.Balance), await db.LedgerEntries.CountAsync());
        });

    [Fact]
    public async Task Create_ReturnsZeroBalanceAccount()
    {
        await using var provider = await MigratedProviderAsync();

        var aliceUser = await NewUserAsync(provider);
        var account = await CreateAsync(provider, aliceUser, "rub");

        Assert.Equal(0, account.Balance);
        Assert.Equal("RUB", account.Currency);
        Assert.Equal("Active", account.Status);
        Assert.False(account.IsSystem);
    }

    [Theory]
    [InlineData("JPY")]
    [InlineData("")]
    public async Task Create_UnsupportedCurrency_Throws(string currency)
    {
        await using var provider = await MigratedProviderAsync();
        var aliceUser = await NewUserAsync(provider);

        await Assert.ThrowsAsync<DomainValidationException>(() => CreateAsync(provider, aliceUser, currency));
    }

    [Fact]
    public async Task Create_TakesOwnerFromUserDisplayName_AndLinksAccountToUser()
    {
        await using var provider = await MigratedProviderAsync();
        var aliceUser = await NewUserAsync(provider);

        var account = await CreateAsync(provider, aliceUser);

        Assert.Equal("Alice", account.Owner);
        var listed = await InScopeAsync(provider, sp => sp.GetRequiredService<AccountsService>().ListAsync(aliceUser, default));
        Assert.Equal(account.Id, Assert.Single(listed).Id);
    }

    [Fact]
    public async Task Create_ForUnknownUser_ThrowsInvalidCredentials()
    {
        await using var provider = await MigratedProviderAsync();

        await Assert.ThrowsAsync<InvalidCredentialsException>(() => CreateAsync(provider, Guid.NewGuid()));
    }

    [Fact]
    public async Task Get_Unknown_ThrowsNotFound()
    {
        await using var provider = await MigratedProviderAsync();
        var aliceUser = await NewUserAsync(provider);

        await Assert.ThrowsAsync<AccountNotFoundException>(() => GetAsync(provider, aliceUser, Guid.NewGuid()));
    }

    [Fact]
    public async Task Deposit_MovesMoneyFromCash()
    {
        await using var provider = await MigratedProviderAsync();
        var aliceUser = await NewUserAsync(provider);
        var account = await CreateAsync(provider, aliceUser);

        await DepositAsync(provider, aliceUser, account.Id, 1000);

        Assert.Equal(1000, (await GetAsync(provider, aliceUser, account.Id)).Balance);
        Assert.Equal(-1000, await CashBalanceAsync(provider));
    }

    [Fact]
    public async Task Deposit_UnknownAccount_ThrowsNotFound()
    {
        await using var provider = await MigratedProviderAsync();
        var aliceUser = await NewUserAsync(provider);

        await Assert.ThrowsAsync<AccountNotFoundException>(() => DepositAsync(provider, aliceUser, Guid.NewGuid(), 100));
    }

    [Fact]
    public async Task Deposit_CurrencyMismatch_Throws()
    {
        await using var provider = await MigratedProviderAsync();
        var aliceUser = await NewUserAsync(provider);
        var account = await CreateAsync(provider, aliceUser);

        await Assert.ThrowsAsync<DomainValidationException>(() => DepositAsync(provider, aliceUser, account.Id, 100, "USD"));
        Assert.Equal((0, 0), await TotalsAsync(provider));
    }

    [Fact]
    public async Task Transfer_HappyPath()
    {
        await using var provider = await MigratedProviderAsync();
        var aliceUser = await NewUserAsync(provider);
        var alice = await CreateAsync(provider, aliceUser);
        var bobUser = await NewUserAsync(provider);
        var bob = await CreateAsync(provider, bobUser);
        await DepositAsync(provider, aliceUser, alice.Id, 1000);

        var transfer = await TransferAsync(provider, aliceUser, alice.Id, bob.Id, 300);

        Assert.Equal(700, (await GetAsync(provider, aliceUser, alice.Id)).Balance);
        Assert.Equal(300, (await GetAsync(provider, bobUser, bob.Id)).Balance);
        var aliceEntry = (await EntriesAsync(provider, aliceUser, alice.Id)).Single(e => e.TransferId == transfer.Id);
        var bobEntry = Assert.Single(await EntriesAsync(provider, bobUser, bob.Id));
        Assert.Equal(-300, aliceEntry.Amount);
        Assert.Equal(700, aliceEntry.BalanceAfter);
        Assert.Equal(300, bobEntry.Amount);
        Assert.Equal(300, bobEntry.BalanceAfter);
        Assert.Equal(0, (await TotalsAsync(provider)).Sum);
    }

    [Fact]
    public async Task Transfer_InsufficientFunds_PersistsNothing()
    {
        await using var provider = await MigratedProviderAsync();
        var aliceUser = await NewUserAsync(provider);
        var alice = await CreateAsync(provider, aliceUser);
        var bobUser = await NewUserAsync(provider);
        var bob = await CreateAsync(provider, bobUser);
        await DepositAsync(provider, aliceUser, alice.Id, 100);
        var before = await TotalsAsync(provider);

        await Assert.ThrowsAsync<InsufficientFundsException>(() => TransferAsync(provider, aliceUser, alice.Id, bob.Id, 101));

        Assert.Equal(100, (await GetAsync(provider, aliceUser, alice.Id)).Balance);
        Assert.Equal(0, (await GetAsync(provider, bobUser, bob.Id)).Balance);
        Assert.Equal(before, await TotalsAsync(provider));
    }

    [Fact]
    public async Task Transfer_ToSameAccount_Throws()
    {
        await using var provider = await MigratedProviderAsync();
        var aliceUser = await NewUserAsync(provider);
        var alice = await CreateAsync(provider, aliceUser);
        await DepositAsync(provider, aliceUser, alice.Id, 100);

        await Assert.ThrowsAsync<DomainValidationException>(() => TransferAsync(provider, aliceUser, alice.Id, alice.Id, 10));
    }

    [Fact]
    public async Task Transfer_BlockedAccount_Throws()
    {
        await using var provider = await MigratedProviderAsync();
        var aliceUser = await NewUserAsync(provider);
        var alice = await CreateAsync(provider, aliceUser);
        var bobUser = await NewUserAsync(provider);
        var bob = await CreateAsync(provider, bobUser);
        await DepositAsync(provider, aliceUser, alice.Id, 100);
        await InScopeAsync(provider, async sp =>
        {
            var db = sp.GetRequiredService<MiniBankDbContext>();
            (await db.Accounts.FindAsync(bob.Id))!.Block();
            await db.SaveChangesAsync();
            return 0;
        });

        await Assert.ThrowsAsync<AccountBlockedException>(() => TransferAsync(provider, aliceUser, alice.Id, bob.Id, 10));
        Assert.Equal(100, (await GetAsync(provider, aliceUser, alice.Id)).Balance);
    }

    [Fact]
    public async Task Transfer_WithCashAsSourceOrTarget_Throws()
    {
        await using var provider = await MigratedProviderAsync();
        var aliceUser = await NewUserAsync(provider);
        var alice = await CreateAsync(provider, aliceUser);
        await DepositAsync(provider, aliceUser, alice.Id, 100);
        var cash = SystemAccounts.CashId("RUB");
        var before = await TotalsAsync(provider);

        // Касса не принадлежит пользователю: как источник она неотличима от несуществующего счёта.
        await Assert.ThrowsAsync<AccountNotFoundException>(() => TransferAsync(provider, aliceUser, cash, alice.Id, 10));
        await Assert.ThrowsAsync<TransferNotAllowedException>(() => TransferAsync(provider, aliceUser, alice.Id, cash, 10));

        Assert.Equal(100, (await GetAsync(provider, aliceUser, alice.Id)).Balance);
        Assert.Equal(-100, await CashBalanceAsync(provider));
        Assert.Equal(before, await TotalsAsync(provider));
    }

    [Fact]
    public async Task Transfer_UnknownAccount_ThrowsNotFound()
    {
        await using var provider = await MigratedProviderAsync();
        var aliceUser = await NewUserAsync(provider);
        var alice = await CreateAsync(provider, aliceUser);
        await DepositAsync(provider, aliceUser, alice.Id, 100);

        await Assert.ThrowsAsync<AccountNotFoundException>(() => TransferAsync(provider, aliceUser, alice.Id, Guid.NewGuid(), 10));
        await Assert.ThrowsAsync<AccountNotFoundException>(() => TransferAsync(provider, aliceUser, Guid.NewGuid(), alice.Id, 10));
    }

    [Theory]
    [InlineData(0, 50)]
    [InlineData(1, 0)]
    [InlineData(1, 101)]
    public async Task GetEntries_InvalidPaging_Throws(int page, int pageSize)
    {
        await using var provider = await MigratedProviderAsync();
        var aliceUser = await NewUserAsync(provider);
        var alice = await CreateAsync(provider, aliceUser);

        await Assert.ThrowsAsync<DomainValidationException>(() => EntriesAsync(provider, aliceUser, alice.Id, page, pageSize));
    }

    [Fact]
    public async Task GetEntries_ReturnsEntriesOfAccount()
    {
        await using var provider = await MigratedProviderAsync();
        var aliceUser = await NewUserAsync(provider);
        var alice = await CreateAsync(provider, aliceUser);
        var bobUser = await NewUserAsync(provider);
        var bob = await CreateAsync(provider, bobUser);
        var carolUser = await NewUserAsync(provider);
        var carol = await CreateAsync(provider, carolUser);
        await DepositAsync(provider, aliceUser, alice.Id, 1000);
        await TransferAsync(provider, aliceUser, alice.Id, bob.Id, 100);

        var aliceEntries = await EntriesAsync(provider, aliceUser, alice.Id);

        Assert.Equal(2, aliceEntries.Count);
        Assert.Empty(await EntriesAsync(provider, carolUser, carol.Id));
        await Assert.ThrowsAsync<AccountNotFoundException>(() => EntriesAsync(provider, aliceUser, Guid.NewGuid()));
    }

    private static Task<(int Transfers, int Keys)> CountsAsync(ServiceProvider provider) =>
        InScopeAsync(provider, async sp =>
        {
            var db = sp.GetRequiredService<MiniBankDbContext>();
            return (await db.Transfers.CountAsync(), await db.Set<IdempotencyKeyRecord>().CountAsync());
        });

    [Fact]
    public async Task Transfer_SameKeyTwice_ReturnsSameResultAndMovesMoneyOnce()
    {
        await using var provider = await MigratedProviderAsync();
        var aliceUser = await NewUserAsync(provider);
        var alice = await CreateAsync(provider, aliceUser);
        var bobUser = await NewUserAsync(provider);
        var bob = await CreateAsync(provider, bobUser);
        await DepositAsync(provider, aliceUser, alice.Id, 1000);

        var first = await TransferWithKeyAsync(provider, aliceUser, "key-1", alice.Id, bob.Id, 300);
        var second = await TransferWithKeyAsync(provider, aliceUser, "key-1", alice.Id, bob.Id, 300);

        Assert.False(first.Replayed);
        Assert.True(second.Replayed);
        Assert.Equal(first.Value, second.Value);
        Assert.Equal(700, (await GetAsync(provider, aliceUser, alice.Id)).Balance);
        Assert.Equal(300, (await GetAsync(provider, bobUser, bob.Id)).Balance);
        Assert.Equal(2, (await EntriesAsync(provider, bobUser, bob.Id)).Count + 1);
    }

    [Fact]
    public async Task Transfer_SameKeyDifferentAmount_ThrowsKeyReuse()
    {
        await using var provider = await MigratedProviderAsync();
        var aliceUser = await NewUserAsync(provider);
        var alice = await CreateAsync(provider, aliceUser);
        var bobUser = await NewUserAsync(provider);
        var bob = await CreateAsync(provider, bobUser);
        await DepositAsync(provider, aliceUser, alice.Id, 1000);
        await TransferWithKeyAsync(provider, aliceUser, "key-1", alice.Id, bob.Id, 300);

        await Assert.ThrowsAsync<IdempotencyKeyReuseException>(() =>
            TransferWithKeyAsync(provider, aliceUser, "key-1", alice.Id, bob.Id, 301));
        Assert.Equal(300, (await GetAsync(provider, bobUser, bob.Id)).Balance);
    }

    [Fact]
    public async Task Deposit_AndTransferWithSameKey_ThrowsKeyReuse()
    {
        await using var provider = await MigratedProviderAsync();
        var aliceUser = await NewUserAsync(provider);
        var alice = await CreateAsync(provider, aliceUser);
        var bobUser = await NewUserAsync(provider);
        var bob = await CreateAsync(provider, bobUser);
        await DepositWithKeyAsync(provider, aliceUser, "key-1", alice.Id, 500);

        await Assert.ThrowsAsync<IdempotencyKeyReuseException>(() =>
            TransferWithKeyAsync(provider, aliceUser, "key-1", alice.Id, bob.Id, 500));
    }

    [Fact]
    public async Task Transfer_LowercaseCurrencyIsSameRequest()
    {
        await using var provider = await MigratedProviderAsync();
        var aliceUser = await NewUserAsync(provider);
        var alice = await CreateAsync(provider, aliceUser);
        var bobUser = await NewUserAsync(provider);
        var bob = await CreateAsync(provider, bobUser);
        await DepositAsync(provider, aliceUser, alice.Id, 1000);

        var first = await TransferWithKeyAsync(provider, aliceUser, "key-1", alice.Id, bob.Id, 100, "RUB");
        var second = await TransferWithKeyAsync(provider, aliceUser, "key-1", alice.Id, bob.Id, 100, "rub");

        Assert.True(second.Replayed);
        Assert.Equal(first.Value, second.Value);
    }

    [Fact]
    public async Task Transfer_FailedByBusinessRule_DoesNotHoldKey()
    {
        await using var provider = await MigratedProviderAsync();
        var aliceUser = await NewUserAsync(provider);
        var alice = await CreateAsync(provider, aliceUser);
        var bobUser = await NewUserAsync(provider);
        var bob = await CreateAsync(provider, bobUser);

        await Assert.ThrowsAsync<InsufficientFundsException>(() =>
            TransferWithKeyAsync(provider, aliceUser, "key-1", alice.Id, bob.Id, 100));
        await DepositAsync(provider, aliceUser, alice.Id, 500);
        var retry = await TransferWithKeyAsync(provider, aliceUser, "key-1", alice.Id, bob.Id, 100);

        Assert.False(retry.Replayed);
        Assert.Equal(100, (await GetAsync(provider, bobUser, bob.Id)).Balance);
    }

    [Fact]
    public async Task Transfer_ParallelSameKey_CreatesExactlyOneTransfer()
    {
        await using var provider = await MigratedProviderAsync();
        var aliceUser = await NewUserAsync(provider);
        var alice = await CreateAsync(provider, aliceUser);
        var bobUser = await NewUserAsync(provider);
        var bob = await CreateAsync(provider, bobUser);
        await DepositAsync(provider, aliceUser, alice.Id, 1000);

        var results = await Task.WhenAll(Enumerable.Range(0, 20)
            .Select(_ => TransferWithKeyAsync(provider, aliceUser, "key-1", alice.Id, bob.Id, 100)));

        Assert.Single(results.Select(r => r.Value).Distinct());
        Assert.Equal(1, results.Count(r => !r.Replayed));
        Assert.Equal(2, (await CountsAsync(provider)).Transfers);
        Assert.Equal(100, (await GetAsync(provider, bobUser, bob.Id)).Balance);
    }

    [Fact]
    public async Task Transfer_ParallelSameKeyBothFail_BothThrowInsufficientFunds()
    {
        await using var provider = await MigratedProviderAsync();
        var aliceUser = await NewUserAsync(provider);
        var alice = await CreateAsync(provider, aliceUser);
        var bobUser = await NewUserAsync(provider);
        var bob = await CreateAsync(provider, bobUser);

        var attempts = Enumerable.Range(0, 2)
            .Select(_ => Assert.ThrowsAsync<InsufficientFundsException>(() =>
                TransferWithKeyAsync(provider, aliceUser, "key-1", alice.Id, bob.Id, 100)));
        await Task.WhenAll(attempts);

        Assert.Equal((0, 0), await CountsAsync(provider));
    }

    public static TheoryData<string?> InvalidKeys => new()
    {
        null,
        "",
        "   ",
        new string('k', 201),
        "a\u0001b"
    };

    [Theory]
    [MemberData(nameof(InvalidKeys))]
    public async Task Key_Invalid_ThrowsValidation(string? key)
    {
        await using var provider = await MigratedProviderAsync();
        var aliceUser = await NewUserAsync(provider);
        var alice = await CreateAsync(provider, aliceUser);
        var bobUser = await NewUserAsync(provider);
        var bob = await CreateAsync(provider, bobUser);

        await Assert.ThrowsAsync<DomainValidationException>(() => DepositWithKeyAsync(provider, aliceUser, key, alice.Id, 100));
        await Assert.ThrowsAsync<DomainValidationException>(() => TransferWithKeyAsync(provider, aliceUser, key, alice.Id, bob.Id, 100));
        Assert.Equal(0, (await GetAsync(provider, aliceUser, alice.Id)).Balance);
    }
}
