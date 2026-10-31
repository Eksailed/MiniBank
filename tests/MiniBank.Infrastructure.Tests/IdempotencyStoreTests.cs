using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MiniBank.Application;
using MiniBank.Application.Abstractions;
using MiniBank.Infrastructure.Persistence;
using MiniBank.TestSupport;

namespace MiniBank.Infrastructure.Tests;

[Collection("postgres")]
public class IdempotencyStoreTests(PostgresFixture postgres)
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

    private static async Task<T> InTransactionAsync<T>(
        ServiceProvider provider, Func<IIdempotencyStore, CancellationToken, Task<T>> operation)
    {
        await using var scope = provider.CreateAsyncScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var store = scope.ServiceProvider.GetRequiredService<IIdempotencyStore>();
        return await uow.ExecuteInTransactionAsync(ct => operation(store, ct), default);
    }

    [Fact]
    public async Task TryReserve_NewKey_ReturnsNull()
    {
        await using var provider = await MigratedProviderAsync();

        var existing = await InTransactionAsync(provider, (store, ct) => store.TryReserveAsync("key-1", "hash-1", ct));

        Assert.Null(existing);
    }

    [Fact]
    public async Task TryReserve_AfterCommittedComplete_ReturnsStoredResponse()
    {
        await using var provider = await MigratedProviderAsync();
        await InTransactionAsync(provider, async (store, ct) =>
        {
            Assert.Null(await store.TryReserveAsync("key-1", "hash-1", ct));
            await store.CompleteAsync("key-1", 200, "{\"a\":1}", ct);
            return 0;
        });

        var stored = await InTransactionAsync(provider, (store, ct) => store.TryReserveAsync("key-1", "other-hash", ct));

        Assert.Equal(new StoredResponse("hash-1", 200, "{\"a\":1}"), stored);
    }

    [Fact]
    public async Task TryReserve_SameKeyInParallelTransaction_WaitsForFirstToFinish()
    {
        await using var provider = await MigratedProviderAsync();
        var reserved = new TaskCompletionSource();
        var release = new TaskCompletionSource();

        var first = InTransactionAsync(provider, async (store, ct) =>
        {
            Assert.Null(await store.TryReserveAsync("key-1", "hash-1", ct));
            reserved.SetResult();
            await release.Task;
            await store.CompleteAsync("key-1", 200, "body", ct);
            return 0;
        });
        await reserved.Task.WaitAsync(Timeout);

        var second = InTransactionAsync(provider, (store, ct) => store.TryReserveAsync("key-1", "hash-1", ct));

        var winner = await Task.WhenAny(second, Task.Delay(500));
        Assert.NotSame(second, winner);

        release.SetResult();
        await first.WaitAsync(Timeout);
        Assert.Equal(new StoredResponse("hash-1", 200, "body"), await second.WaitAsync(Timeout));
    }

    [Fact]
    public async Task TryReserve_AfterRollback_ReservesAgain()
    {
        await using var provider = await MigratedProviderAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            InTransactionAsync<int>(provider, async (store, ct) =>
            {
                Assert.Null(await store.TryReserveAsync("key-1", "hash-1", ct));
                throw new InvalidOperationException("boom");
            }));

        var existing = await InTransactionAsync(provider, (store, ct) => store.TryReserveAsync("key-1", "hash-1", ct));

        Assert.Null(existing);
    }
}
