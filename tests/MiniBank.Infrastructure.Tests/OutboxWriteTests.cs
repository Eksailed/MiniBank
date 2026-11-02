using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MiniBank.Application;
using MiniBank.Contracts;
using MiniBank.Domain;
using MiniBank.Infrastructure.Persistence;
using MiniBank.TestSupport;

namespace MiniBank.Infrastructure.Tests;

[Collection("postgres")]
public class OutboxWriteTests(PostgresFixture postgres)
{
    private static readonly DateTimeOffset Now = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);

    private async Task<ServiceProvider> ProviderAsync()
    {
        var provider = new ServiceCollection()
            .AddInfrastructure(await postgres.CreateDatabaseAsync())
            .AddApplication()
            .AddSingleton<TimeProvider>(new FakeClock(Now))
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

    private static Task<AccountDto> OpenAsync(ServiceProvider provider, Guid userId) =>
        InScopeAsync(provider, sp => sp.GetRequiredService<AccountsService>().CreateAsync(userId, "RUB", default));

    private static Task<IdempotentResult<TransferDto>> DepositAsync(
        ServiceProvider provider, Guid userId, string key, Guid accountId, long amount) =>
        InScopeAsync(provider, sp => sp.GetRequiredService<TransfersService>().DepositAsync(userId, key, accountId, amount, "RUB", default));

    private static Task<IdempotentResult<TransferDto>> TransferAsync(
        ServiceProvider provider, Guid userId, string key, Guid from, Guid to, long amount) =>
        InScopeAsync(provider, sp => sp.GetRequiredService<TransfersService>().TransferAsync(userId, key, from, to, amount, "RUB", default));

    private static Task<List<OutboxMessage>> OutboxAsync(ServiceProvider provider) =>
        InScopeAsync(provider, sp => sp.GetRequiredService<MiniBankDbContext>().OutboxMessages.AsNoTracking().ToListAsync());

    [Fact]
    public async Task Transfer_AddsOneTransferCompletedEvent()
    {
        var provider = await ProviderAsync();
        var alice = await NewUserAsync(provider);
        var bob = await NewUserAsync(provider);
        var from = await OpenAsync(provider, alice);
        var to = await OpenAsync(provider, bob);
        await DepositAsync(provider, alice, "d1", from.Id, 10_000);
        var before = (await OutboxAsync(provider)).Count;

        // Перевод между счетами разных владельцев: bob владеет получателем, но переводит alice.
        var transfer = (await TransferAsync(provider, alice, "t1", from.Id, to.Id, 2_500)).Value;

        var added = (await OutboxAsync(provider)).Where(m => m.Type == EventTypes.TransferCompleted).ToList();
        var message = Assert.Single(added);
        Assert.Equal(before + 1, (await OutboxAsync(provider)).Count);
        Assert.Null(message.PublishedAt);
        Assert.Equal(0, message.Attempts);
        Assert.Equal(1, message.Version);
        Assert.Equal(Now, message.OccurredAt);

        var data = JsonDocument.Parse(message.Payload).RootElement;
        Assert.Equal(transfer.Id, data.GetProperty("transferId").GetGuid());
        Assert.Equal(from.Id, data.GetProperty("fromAccountId").GetGuid());
        Assert.Equal(to.Id, data.GetProperty("toAccountId").GetGuid());
        Assert.Equal(alice, data.GetProperty("fromOwnerId").GetGuid());
        Assert.Equal(bob, data.GetProperty("toOwnerId").GetGuid());
        Assert.Equal(2_500, data.GetProperty("amount").GetInt64());
        Assert.Equal("RUB", data.GetProperty("currency").GetString());
    }

    [Fact]
    public async Task Deposit_AddsOneAccountDepositedEvent()
    {
        var provider = await ProviderAsync();
        var alice = await NewUserAsync(provider);
        var account = await OpenAsync(provider, alice);

        var transfer = (await DepositAsync(provider, alice, "d1", account.Id, 7_000)).Value;

        var message = Assert.Single(await OutboxAsync(provider));
        Assert.Equal(EventTypes.AccountDeposited, message.Type);
        var data = JsonDocument.Parse(message.Payload).RootElement;
        Assert.Equal(transfer.Id, data.GetProperty("transferId").GetGuid());
        Assert.Equal(account.Id, data.GetProperty("accountId").GetGuid());
        Assert.Equal(alice, data.GetProperty("ownerId").GetGuid());
        Assert.Equal(7_000, data.GetProperty("amount").GetInt64());
        Assert.Equal("RUB", data.GetProperty("currency").GetString());
    }

    [Fact]
    public async Task FailedTransfer_AddsNoEvent()
    {
        var provider = await ProviderAsync();
        var alice = await NewUserAsync(provider);
        var bob = await NewUserAsync(provider);
        var from = await OpenAsync(provider, alice);
        var to = await OpenAsync(provider, alice);
        var foreign = await OpenAsync(provider, bob);

        await Assert.ThrowsAnyAsync<Exception>(() => TransferAsync(provider, alice, "t1", from.Id, to.Id, 1_000)); // нет средств
        await Assert.ThrowsAnyAsync<Exception>(() => TransferAsync(provider, alice, "t2", foreign.Id, to.Id, 1_000)); // чужой счёт

        Assert.Empty(await OutboxAsync(provider));
    }

    [Fact]
    public async Task IdempotentReplay_DoesNotDuplicateEvent()
    {
        var provider = await ProviderAsync();
        var alice = await NewUserAsync(provider);
        var from = await OpenAsync(provider, alice);
        var to = await OpenAsync(provider, alice);

        var firstDeposit = await DepositAsync(provider, alice, "dep", from.Id, 10_000);
        var secondDeposit = await DepositAsync(provider, alice, "dep", from.Id, 10_000);
        var firstTransfer = await TransferAsync(provider, alice, "tr", from.Id, to.Id, 1_000);
        var secondTransfer = await TransferAsync(provider, alice, "tr", from.Id, to.Id, 1_000);

        Assert.False(firstDeposit.Replayed);
        Assert.True(secondDeposit.Replayed);
        Assert.False(firstTransfer.Replayed);
        Assert.True(secondTransfer.Replayed);
        var messages = await OutboxAsync(provider);
        Assert.Equal(2, messages.Count);
        Assert.Single(messages, m => m.Type == EventTypes.AccountDeposited);
        Assert.Single(messages, m => m.Type == EventTypes.TransferCompleted);
    }

    [Fact]
    public async Task Payload_ContainsNoEmailOrNames()
    {
        var provider = await ProviderAsync();
        var alice = await NewUserAsync(provider);
        var from = await OpenAsync(provider, alice);
        var to = await OpenAsync(provider, alice);
        await DepositAsync(provider, alice, "dep", from.Id, 10_000);
        await TransferAsync(provider, alice, "tr", from.Id, to.Id, 1_000);

        foreach (var message in await OutboxAsync(provider))
        {
            Assert.DoesNotContain("email", message.Payload, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("displayName", message.Payload, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Alice", message.Payload);
        }
    }
}
