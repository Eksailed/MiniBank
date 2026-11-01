using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MiniBank.Application;
using MiniBank.Application.Abstractions;
using MiniBank.Domain;
using MiniBank.Infrastructure.Persistence;
using MiniBank.TestSupport;

namespace MiniBank.Infrastructure.Tests;

[Collection("postgres")]
public class RepositoryTests(PostgresFixture postgres)
{
    private static readonly DateTimeOffset Now = new(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);

    private async Task<ServiceProvider> MigratedProviderAsync()
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        var provider = new ServiceCollection().AddInfrastructure(connectionString).BuildServiceProvider();

        await using var scope = provider.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<MiniBankDbContext>().Database.MigrateAsync();
        return provider;
    }

    private static async Task<Guid> SeedUserAsync(ServiceProvider provider)
    {
        await using var scope = provider.CreateAsyncScope();
        return (await TestData.SeedUserAsync(scope.ServiceProvider.GetRequiredService<MiniBankDbContext>())).Id;
    }

    [Fact]
    public async Task Add_ThenFind_ReturnsAccount()
    {
        await using var provider = await MigratedProviderAsync();
        var userId = await SeedUserAsync(provider);
        var account = TestData.OpenFor(userId);

        await using (var scope = provider.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<IAccountRepository>().Add(account);
            await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync(CancellationToken.None);
        }

        await using var readScope = provider.CreateAsyncScope();
        var found = await readScope.ServiceProvider.GetRequiredService<IAccountRepository>()
            .FindAsync(account.Id, CancellationToken.None);

        Assert.NotNull(found);
        Assert.Equal("Alice", found.Owner);
        Assert.Equal("RUB", found.Currency);
        Assert.Equal(0, found.Balance);
    }

    [Fact]
    public async Task UserQueries_ListCountAndOwnership_AreScopedToTheUser()
    {
        await using var provider = await MigratedProviderAsync();
        var alice = await SeedUserAsync(provider);
        var bob = await SeedUserAsync(provider);
        var first = Account.Open("Alice", "RUB", Now, userId: alice, limits: TestData.DefaultLimits);
        var second = Account.Open("Alice", "USD", Now.AddMinutes(1), userId: alice, limits: TestData.DefaultLimits);
        var bobs = TestData.OpenFor(bob);
        await using (var scope = provider.CreateAsyncScope())
        {
            var accounts = scope.ServiceProvider.GetRequiredService<IAccountRepository>();
            accounts.Add(second);
            accounts.Add(first);
            accounts.Add(bobs);
            await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync(CancellationToken.None);
        }

        await using var readScope = provider.CreateAsyncScope();
        var repository = readScope.ServiceProvider.GetRequiredService<IAccountRepository>();

        var listed = await repository.ListByUserAsync(alice, CancellationToken.None);

        Assert.Equal([first.Id, second.Id], listed.Select(a => a.Id));
        Assert.Equal(2, await repository.CountByUserAsync(alice, CancellationToken.None));
        Assert.Equal(0, await repository.CountByUserAsync(Guid.NewGuid(), CancellationToken.None));
        Assert.True(await repository.IsOwnedByAsync(first.Id, alice, CancellationToken.None));
        Assert.False(await repository.IsOwnedByAsync(bobs.Id, alice, CancellationToken.None));
        Assert.False(await repository.IsOwnedByAsync(SystemAccounts.CashId("RUB"), alice, CancellationToken.None));
    }

    [Fact]
    public async Task Find_Unknown_ReturnsNull()
    {
        await using var provider = await MigratedProviderAsync();
        await using var scope = provider.CreateAsyncScope();

        var found = await scope.ServiceProvider.GetRequiredService<IAccountRepository>()
            .FindAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.Null(found);
    }

    [Fact]
    public async Task SaveChanges_OnStaleVersion_ThrowsConcurrencyConflict()
    {
        await using var provider = await MigratedProviderAsync();
        var userId = await SeedUserAsync(provider);
        var account = TestData.OpenFor(userId);
        await using (var scope = provider.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<IAccountRepository>().Add(account);
            await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync(CancellationToken.None);
        }

        await using var first = provider.CreateAsyncScope();
        await using var second = provider.CreateAsyncScope();
        var firstAccount = await first.ServiceProvider.GetRequiredService<IAccountRepository>()
            .FindAsync(account.Id, CancellationToken.None);
        var secondAccount = await second.ServiceProvider.GetRequiredService<IAccountRepository>()
            .FindAsync(account.Id, CancellationToken.None);

        firstAccount!.Credit(Guid.NewGuid(), Money.Of(100, "RUB"), Now);
        secondAccount!.Credit(Guid.NewGuid(), Money.Of(100, "RUB"), Now);

        await first.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync(CancellationToken.None);
        await Assert.ThrowsAsync<ConcurrencyConflictException>(() =>
            second.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync(CancellationToken.None));
    }

    [Fact]
    public async Task GetEntries_ReturnsNewestFirstAndPages()
    {
        await using var provider = await MigratedProviderAsync();
        var userId = await SeedUserAsync(provider);
        var account = TestData.OpenFor(userId);

        await using (var scope = provider.CreateAsyncScope())
        {
            var accounts = scope.ServiceProvider.GetRequiredService<IAccountRepository>();
            var transfers = scope.ServiceProvider.GetRequiredService<ITransferRepository>();
            var cash = await accounts.FindAsync(SystemAccounts.CashId("RUB"), CancellationToken.None);
            accounts.Add(account);

            for (var i = 0; i < 5; i++)
                transfers.Add(Transfer.Execute(cash!, account, Money.Of(10, "RUB"), Now.AddMinutes(i)));

            await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync(CancellationToken.None);
        }

        await using var readScope = provider.CreateAsyncScope();
        var ledger = readScope.ServiceProvider.GetRequiredService<ILedgerQuery>();

        var page1 = await ledger.GetEntriesAsync(account.Id, 1, 2, CancellationToken.None);
        var page2 = await ledger.GetEntriesAsync(account.Id, 2, 2, CancellationToken.None);
        var page3 = await ledger.GetEntriesAsync(account.Id, 3, 2, CancellationToken.None);

        Assert.Equal([2, 2, 1], [page1.Count, page2.Count, page3.Count]);
        var all = page1.Concat(page2).Concat(page3).ToList();
        Assert.Equal(all.OrderByDescending(e => e.CreatedAt).Select(e => e.Id), all.Select(e => e.Id));
        Assert.Equal(Now.AddMinutes(4), all[0].CreatedAt);
        Assert.Equal(Now, all[4].CreatedAt);
        Assert.All(all, e => Assert.Equal(account.Id, e.AccountId));
    }
}
