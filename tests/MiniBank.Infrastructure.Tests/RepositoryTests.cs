using MiniBank.Application.Statements;
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

        var page1 = await ledger.GetEntriesAsync(account.Id, EntryFilter.None, 1, 2, CancellationToken.None);
        var page2 = await ledger.GetEntriesAsync(account.Id, EntryFilter.None, 2, 2, CancellationToken.None);
        var page3 = await ledger.GetEntriesAsync(account.Id, EntryFilter.None, 3, 2, CancellationToken.None);

        Assert.Equal([2, 2, 1], [page1.Count, page2.Count, page3.Count]);
        var all = page1.Concat(page2).Concat(page3).ToList();
        Assert.Equal(all.OrderByDescending(e => e.CreatedAt).Select(e => e.Id), all.Select(e => e.Id));
        Assert.Equal(Now.AddMinutes(4), all[0].CreatedAt);
        Assert.Equal(Now, all[4].CreatedAt);
        Assert.All(all, e => Assert.Equal(account.Id, e.AccountId));
    }

    [Fact]
    public async Task ResetToken_RoundTripsAndIsFoundByHash()
    {
        await using var provider = await MigratedProviderAsync();
        var userId = await SeedUserAsync(provider);

        await using (var scope = provider.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<IUserRepository>().Add(PasswordResetToken.Create(userId, "h1", Now));
            await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync(CancellationToken.None);
        }

        await using var readScope = provider.CreateAsyncScope();
        var repo = readScope.ServiceProvider.GetRequiredService<IUserRepository>();
        var uow = readScope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var found = await uow.ExecuteInTransactionAsync(ct => repo.FindResetTokenByHashAsync("h1", ct), CancellationToken.None);

        Assert.NotNull(found);
        Assert.Equal(userId, found.UserId);
        Assert.Equal(Now.AddHours(1), found.ExpiresAt);
        Assert.Null(await uow.ExecuteInTransactionAsync(ct => repo.FindResetTokenByHashAsync("nope", ct), CancellationToken.None));
    }

    [Fact]
    public async Task FindLatestResetToken_ReturnsNewest()
    {
        await using var provider = await MigratedProviderAsync();
        var userId = await SeedUserAsync(provider);

        await using (var scope = provider.CreateAsyncScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<IUserRepository>();
            repo.Add(PasswordResetToken.Create(userId, "old", Now));
            repo.Add(PasswordResetToken.Create(userId, "new", Now.AddMinutes(5)));
            await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync(CancellationToken.None);
        }

        await using var readScope = provider.CreateAsyncScope();
        var latest = await readScope.ServiceProvider.GetRequiredService<IUserRepository>()
            .FindLatestResetTokenAsync(userId, CancellationToken.None);

        Assert.NotNull(latest);
        Assert.Equal(Now.AddMinutes(5), latest.CreatedAt);
    }

    [Fact]
    public async Task InvalidateOpenResetTokens_MarksOnlyOpenTokensOfThatUser()
    {
        await using var provider = await MigratedProviderAsync();
        var userId = await SeedUserAsync(provider);
        Guid otherId;
        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MiniBankDbContext>();
            var other = User.Register("other@b.c", "hash", "Other", Now);
            db.Users.Add(other);
            otherId = other.Id;
            var repo = scope.ServiceProvider.GetRequiredService<IUserRepository>();
            var used = PasswordResetToken.Create(userId, "used", Now);
            used.MarkUsed(Now.AddMinutes(1));
            repo.Add(used);
            repo.Add(PasswordResetToken.Create(userId, "open", Now));
            repo.Add(PasswordResetToken.Create(otherId, "foreign", Now));
            await db.SaveChangesAsync();
        }

        await using (var scope = provider.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IUserRepository>()
                .InvalidateOpenResetTokensAsync(userId, Now.AddMinutes(10), CancellationToken.None);
        }

        await using var readScope = provider.CreateAsyncScope();
        var tokens = await readScope.ServiceProvider.GetRequiredService<MiniBankDbContext>()
            .PasswordResetTokens.AsNoTracking().ToDictionaryAsync(t => t.TokenHash);
        Assert.Equal(Now.AddMinutes(1), tokens["used"].UsedAt);
        Assert.Equal(Now.AddMinutes(10), tokens["open"].UsedAt);
        Assert.Null(tokens["foreign"].UsedAt);
    }

    [Fact]
    public async Task User_PasswordChangedAt_IsPersisted()
    {
        await using var provider = await MigratedProviderAsync();
        var userId = await SeedUserAsync(provider);

        await using (var scope = provider.CreateAsyncScope())
        {
            var user = await scope.ServiceProvider.GetRequiredService<IUserRepository>().FindByIdAsync(userId, CancellationToken.None);
            Assert.Null(user!.PasswordChangedAt);
            user.ChangePassword("new-hash", Now);
            await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync(CancellationToken.None);
        }

        await using var readScope = provider.CreateAsyncScope();
        var loaded = await readScope.ServiceProvider.GetRequiredService<MiniBankDbContext>()
            .Users.AsNoTracking().SingleAsync(u => u.Id == userId);
        Assert.Equal(Now, loaded.PasswordChangedAt);
        Assert.Equal("new-hash", loaded.PasswordHash);
    }
}
