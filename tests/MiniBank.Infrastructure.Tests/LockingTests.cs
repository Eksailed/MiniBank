using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MiniBank.Application;
using MiniBank.Application.Abstractions;
using MiniBank.Domain;
using MiniBank.Infrastructure.Persistence;
using MiniBank.TestSupport;
using Npgsql;

namespace MiniBank.Infrastructure.Tests;

[Collection("postgres")]
public class LockingTests(PostgresFixture postgres)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

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

    private static async Task<Guid> CreateAccountAsync(ServiceProvider provider)
    {
        await using var scope = provider.CreateAsyncScope();
        var accounts = scope.ServiceProvider.GetRequiredService<IAccountRepository>();
        var account = Account.Open("owner", "RUB", DateTimeOffset.UtcNow);
        accounts.Add(account);
        await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync(default);
        return account.Id;
    }

    private static async Task<T> InTransactionAsync<T>(
        ServiceProvider provider, Func<IServiceProvider, CancellationToken, Task<T>> operation)
    {
        await using var scope = provider.CreateAsyncScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        return await uow.ExecuteInTransactionAsync(ct => operation(scope.ServiceProvider, ct), default);
    }

    [Fact]
    public async Task FindForUpdate_ReturnsExistingAccountsAndSkipsMissing()
    {
        await using var provider = await MigratedProviderAsync();
        var id = await CreateAccountAsync(provider);

        var found = await InTransactionAsync(provider, (sp, ct) =>
            sp.GetRequiredService<IAccountRepository>().FindForUpdateAsync([id, Guid.NewGuid(), id], ct));

        var account = Assert.Single(found);
        Assert.Equal(id, account.Key);
        Assert.Equal(id, account.Value.Id);
    }

    [Fact]
    public async Task FindForUpdate_BlocksSecondTransactionUntilFirstCommits()
    {
        await using var provider = await MigratedProviderAsync();
        var id = await CreateAccountAsync(provider);
        var firstLocked = new TaskCompletionSource();
        var releaseFirst = new TaskCompletionSource();

        var first = InTransactionAsync(provider, async (sp, ct) =>
        {
            await sp.GetRequiredService<IAccountRepository>().FindForUpdateAsync([id], ct);
            firstLocked.SetResult();
            await releaseFirst.Task;
            return 0;
        });
        await firstLocked.Task.WaitAsync(Timeout);

        var second = InTransactionAsync(provider, (sp, ct) =>
            sp.GetRequiredService<IAccountRepository>().FindForUpdateAsync([id], ct));

        var winner = await Task.WhenAny(second, Task.Delay(500));
        Assert.NotSame(second, winner);

        releaseFirst.SetResult();
        await first.WaitAsync(Timeout);
        Assert.Single(await second.WaitAsync(Timeout));
    }

    [Fact]
    public async Task ExecuteInTransaction_RollsBackOnException()
    {
        await using var provider = await MigratedProviderAsync();
        var created = Guid.Empty;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            InTransactionAsync<int>(provider, async (sp, ct) =>
            {
                var account = Account.Open("owner", "RUB", DateTimeOffset.UtcNow);
                created = account.Id;
                sp.GetRequiredService<IAccountRepository>().Add(account);
                await sp.GetRequiredService<IUnitOfWork>().SaveChangesAsync(ct);
                throw new InvalidOperationException("boom");
            }));

        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MiniBankDbContext>();
        Assert.False(await db.Accounts.AnyAsync(a => a.Id == created));
    }

    [Fact]
    public async Task ExecuteInTransaction_RetriesAfterDeadlock()
    {
        await using var provider = await MigratedProviderAsync();
        var x = await CreateAccountAsync(provider);
        var y = await CreateAccountAsync(provider);
        var xLocked = new TaskCompletionSource();
        var yLocked = new TaskCompletionSource();
        var runs = 0;

        Task<int> Run(Guid mine, Guid other, TaskCompletionSource myLocked, TaskCompletionSource otherLocked)
        {
            var attempts = 0;
            return InTransactionAsync(provider, async (sp, ct) =>
            {
                Interlocked.Increment(ref runs);
                var accounts = sp.GetRequiredService<IAccountRepository>();
                if (++attempts == 1)
                {
                    await accounts.FindForUpdateAsync([mine], ct);
                    myLocked.TrySetResult();
                    await otherLocked.Task;
                    await accounts.FindForUpdateAsync([other], ct);
                }
                else
                {
                    await accounts.FindForUpdateAsync([x, y], ct);
                }
                return attempts;
            });
        }

        var results = await Task.WhenAll(Run(x, y, xLocked, yLocked), Run(y, x, yLocked, xLocked)).WaitAsync(Timeout);

        Assert.Equal(3, runs);
        Assert.Equal([1, 2], results.Order().ToArray());
    }

    [Fact]
    public async Task ExecuteInTransaction_ThrowsConcurrencyConflictAfterThreeAttempts()
    {
        await using var provider = await MigratedProviderAsync();
        var runs = 0;

        await Assert.ThrowsAsync<ConcurrencyConflictException>(() =>
            InTransactionAsync<int>(provider, (_, _) =>
            {
                runs++;
                throw new PostgresException("deadlock", "ERROR", "ERROR", "40P01");
            }));

        Assert.Equal(3, runs);
    }

    [Fact]
    public async Task ExecuteInTransaction_DoesNotRetryOtherExceptions()
    {
        await using var provider = await MigratedProviderAsync();
        var runs = 0;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            InTransactionAsync<int>(provider, (_, _) =>
            {
                runs++;
                throw new InvalidOperationException("boom");
            }));

        Assert.Equal(1, runs);
    }
}
