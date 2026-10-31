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

    private static Task<AccountDto> CreateAsync(ServiceProvider provider, string owner, string currency = "RUB") =>
        InScopeAsync(provider, sp => sp.GetRequiredService<AccountsService>().CreateAsync(owner, currency, default));

    private static Task<TransferDto> DepositAsync(ServiceProvider provider, Guid id, long amount, string currency = "RUB") =>
        InScopeAsync(provider, sp => sp.GetRequiredService<TransfersService>().DepositAsync(id, amount, currency, default));

    private static Task<TransferDto> TransferAsync(ServiceProvider provider, Guid from, Guid to, long amount, string currency = "RUB") =>
        InScopeAsync(provider, sp => sp.GetRequiredService<TransfersService>().TransferAsync(from, to, amount, currency, default));

    private static Task<AccountDto> GetAsync(ServiceProvider provider, Guid id) =>
        InScopeAsync(provider, sp => sp.GetRequiredService<AccountsService>().GetAsync(id, default));

    private static Task<IReadOnlyList<EntryDto>> EntriesAsync(ServiceProvider provider, Guid id, int page = 1, int pageSize = 50) =>
        InScopeAsync(provider, sp => sp.GetRequiredService<AccountsService>().GetEntriesAsync(id, page, pageSize, default));

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

        var account = await CreateAsync(provider, "Alice", "rub");

        Assert.Equal(0, account.Balance);
        Assert.Equal("RUB", account.Currency);
        Assert.Equal("Active", account.Status);
        Assert.False(account.IsSystem);
    }

    [Theory]
    [InlineData("Alice", "JPY")]
    [InlineData("", "RUB")]
    public async Task Create_UnsupportedCurrencyOrEmptyOwner_Throws(string owner, string currency)
    {
        await using var provider = await MigratedProviderAsync();

        await Assert.ThrowsAsync<DomainValidationException>(() => CreateAsync(provider, owner, currency));
    }

    [Fact]
    public async Task Get_Unknown_ThrowsNotFound()
    {
        await using var provider = await MigratedProviderAsync();

        await Assert.ThrowsAsync<AccountNotFoundException>(() => GetAsync(provider, Guid.NewGuid()));
    }

    [Fact]
    public async Task Deposit_MovesMoneyFromCash()
    {
        await using var provider = await MigratedProviderAsync();
        var account = await CreateAsync(provider, "Alice");

        await DepositAsync(provider, account.Id, 1000);

        Assert.Equal(1000, (await GetAsync(provider, account.Id)).Balance);
        Assert.Equal(-1000, (await GetAsync(provider, SystemAccounts.CashId("RUB"))).Balance);
    }

    [Fact]
    public async Task Deposit_UnknownAccount_ThrowsNotFound()
    {
        await using var provider = await MigratedProviderAsync();

        await Assert.ThrowsAsync<AccountNotFoundException>(() => DepositAsync(provider, Guid.NewGuid(), 100));
    }

    [Fact]
    public async Task Deposit_CurrencyMismatch_Throws()
    {
        await using var provider = await MigratedProviderAsync();
        var account = await CreateAsync(provider, "Alice");

        await Assert.ThrowsAsync<DomainValidationException>(() => DepositAsync(provider, account.Id, 100, "USD"));
        Assert.Equal((0, 0), await TotalsAsync(provider));
    }

    [Fact]
    public async Task Transfer_HappyPath()
    {
        await using var provider = await MigratedProviderAsync();
        var alice = await CreateAsync(provider, "Alice");
        var bob = await CreateAsync(provider, "Bob");
        await DepositAsync(provider, alice.Id, 1000);

        var transfer = await TransferAsync(provider, alice.Id, bob.Id, 300);

        Assert.Equal(700, (await GetAsync(provider, alice.Id)).Balance);
        Assert.Equal(300, (await GetAsync(provider, bob.Id)).Balance);
        var aliceEntry = (await EntriesAsync(provider, alice.Id)).Single(e => e.TransferId == transfer.Id);
        var bobEntry = Assert.Single(await EntriesAsync(provider, bob.Id));
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
        var alice = await CreateAsync(provider, "Alice");
        var bob = await CreateAsync(provider, "Bob");
        await DepositAsync(provider, alice.Id, 100);
        var before = await TotalsAsync(provider);

        await Assert.ThrowsAsync<InsufficientFundsException>(() => TransferAsync(provider, alice.Id, bob.Id, 101));

        Assert.Equal(100, (await GetAsync(provider, alice.Id)).Balance);
        Assert.Equal(0, (await GetAsync(provider, bob.Id)).Balance);
        Assert.Equal(before, await TotalsAsync(provider));
    }

    [Fact]
    public async Task Transfer_ToSameAccount_Throws()
    {
        await using var provider = await MigratedProviderAsync();
        var alice = await CreateAsync(provider, "Alice");
        await DepositAsync(provider, alice.Id, 100);

        await Assert.ThrowsAsync<DomainValidationException>(() => TransferAsync(provider, alice.Id, alice.Id, 10));
    }

    [Fact]
    public async Task Transfer_BlockedAccount_Throws()
    {
        await using var provider = await MigratedProviderAsync();
        var alice = await CreateAsync(provider, "Alice");
        var bob = await CreateAsync(provider, "Bob");
        await DepositAsync(provider, alice.Id, 100);
        await InScopeAsync(provider, async sp =>
        {
            var db = sp.GetRequiredService<MiniBankDbContext>();
            (await db.Accounts.FindAsync(bob.Id))!.Block();
            await db.SaveChangesAsync();
            return 0;
        });

        await Assert.ThrowsAsync<AccountBlockedException>(() => TransferAsync(provider, alice.Id, bob.Id, 10));
        Assert.Equal(100, (await GetAsync(provider, alice.Id)).Balance);
    }

    [Fact]
    public async Task Transfer_WithCashAsSourceOrTarget_Throws()
    {
        await using var provider = await MigratedProviderAsync();
        var alice = await CreateAsync(provider, "Alice");
        await DepositAsync(provider, alice.Id, 100);
        var cash = SystemAccounts.CashId("RUB");
        var before = await TotalsAsync(provider);

        await Assert.ThrowsAsync<TransferNotAllowedException>(() => TransferAsync(provider, cash, alice.Id, 10));
        await Assert.ThrowsAsync<TransferNotAllowedException>(() => TransferAsync(provider, alice.Id, cash, 10));

        Assert.Equal(100, (await GetAsync(provider, alice.Id)).Balance);
        Assert.Equal(-100, (await GetAsync(provider, cash)).Balance);
        Assert.Equal(before, await TotalsAsync(provider));
    }

    [Fact]
    public async Task Transfer_UnknownAccount_ThrowsNotFound()
    {
        await using var provider = await MigratedProviderAsync();
        var alice = await CreateAsync(provider, "Alice");
        await DepositAsync(provider, alice.Id, 100);

        await Assert.ThrowsAsync<AccountNotFoundException>(() => TransferAsync(provider, alice.Id, Guid.NewGuid(), 10));
        await Assert.ThrowsAsync<AccountNotFoundException>(() => TransferAsync(provider, Guid.NewGuid(), alice.Id, 10));
    }

    [Theory]
    [InlineData(0, 50)]
    [InlineData(1, 0)]
    [InlineData(1, 101)]
    public async Task GetEntries_InvalidPaging_Throws(int page, int pageSize)
    {
        await using var provider = await MigratedProviderAsync();
        var alice = await CreateAsync(provider, "Alice");

        await Assert.ThrowsAsync<DomainValidationException>(() => EntriesAsync(provider, alice.Id, page, pageSize));
    }

    [Fact]
    public async Task GetEntries_ReturnsEntriesOfAccount()
    {
        await using var provider = await MigratedProviderAsync();
        var alice = await CreateAsync(provider, "Alice");
        var bob = await CreateAsync(provider, "Bob");
        var carol = await CreateAsync(provider, "Carol");
        await DepositAsync(provider, alice.Id, 1000);
        await TransferAsync(provider, alice.Id, bob.Id, 100);

        var aliceEntries = await EntriesAsync(provider, alice.Id);

        Assert.Equal(2, aliceEntries.Count);
        Assert.Empty(await EntriesAsync(provider, carol.Id));
        await Assert.ThrowsAsync<AccountNotFoundException>(() => EntriesAsync(provider, Guid.NewGuid()));
    }
}
