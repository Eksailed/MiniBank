using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using MiniBank.Contracts;
using MiniBank.TestSupport;

namespace MiniBank.Notifications.Tests;

[Collection("postgres")]
public class DeliveryEnqueueTests(PostgresFixture postgres)
{
    private static readonly DateTimeOffset Occurred = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);

    private sealed class Factory(string connectionString) : IDbContextFactory<NotificationsDbContext>
    {
        public NotificationsDbContext CreateDbContext() => new(NotificationsDbContext.CreateOptions(connectionString));
    }

    private async Task<(Factory Db, EventHandler Handler)> SetupAsync()
    {
        var db = new Factory(await postgres.CreateDatabaseAsync());
        await using (var context = db.CreateDbContext())
            await context.Database.MigrateAsync();
        return (db, new EventHandler(db, NullLogger<EventHandler>.Instance));
    }

    private static async Task LinkAsync(Factory db, Guid userId, long chatId)
    {
        await using var context = db.CreateDbContext();
        context.TelegramLinks.Add(new TelegramLink { UserId = userId, ChatId = chatId, LinkedAt = Occurred });
        await context.SaveChangesAsync();
    }

    private static byte[] Body(Guid eventId, string type, object data) =>
        JsonSerializer.SerializeToUtf8Bytes(
            new EventEnvelope(eventId, type, 1, Occurred, JsonSerializer.SerializeToElement(data, EventJson.Options)),
            EventJson.Options);

    private static byte[] Transfer(Guid eventId, Guid from, Guid? to) =>
        Body(eventId, EventTypes.TransferCompleted,
            new TransferCompleted(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), from, to, 250_000, "RUB"));

    private static async Task<List<TelegramDelivery>> DeliveriesAsync(Factory db)
    {
        await using var context = db.CreateDbContext();
        return await context.TelegramDeliveries.OrderBy(d => d.ChatId).ToListAsync();
    }

    [Fact]
    public async Task Transfer_BetweenLinkedOwners_QueuesTwoMessages()
    {
        var (db, handler) = await SetupAsync();
        Guid alice = Guid.NewGuid(), bob = Guid.NewGuid();
        await LinkAsync(db, alice, 1);
        await LinkAsync(db, bob, 2);

        await handler.HandleAsync(Transfer(Guid.NewGuid(), alice, bob), default);

        var deliveries = await DeliveriesAsync(db);
        Assert.Equal(2, deliveries.Count);
        Assert.Equal("Перевод: −2 500,00 ₽", deliveries[0].Text);
        Assert.Equal("Поступление: +2 500,00 ₽", deliveries[1].Text);
        Assert.All(deliveries, d => Assert.Equal(DeliveryStatus.Pending, d.Status));
    }

    [Fact]
    public async Task Deposit_QueuesOneMessage()
    {
        var (db, handler) = await SetupAsync();
        var owner = Guid.NewGuid();
        await LinkAsync(db, owner, 7);

        await handler.HandleAsync(
            Body(Guid.NewGuid(), EventTypes.AccountDeposited, new AccountDeposited(Guid.NewGuid(), Guid.NewGuid(), owner, 1_000_000, "RUB")),
            default);

        var delivery = Assert.Single(await DeliveriesAsync(db));
        Assert.Equal("Пополнение: +10 000,00 ₽", delivery.Text);
        Assert.Equal(7, delivery.ChatId);
    }

    [Fact]
    public async Task OwnerWithoutLink_GetsNoDelivery()
    {
        var (db, handler) = await SetupAsync();
        Guid alice = Guid.NewGuid(), bob = Guid.NewGuid();
        await LinkAsync(db, bob, 2);

        await handler.HandleAsync(Transfer(Guid.NewGuid(), alice, bob), default);

        var delivery = Assert.Single(await DeliveriesAsync(db));
        Assert.Equal(2, delivery.ChatId);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Settings_SuppressDeliveries(bool disableAll, bool disableSent)
    {
        var (db, handler) = await SetupAsync();
        Guid alice = Guid.NewGuid(), bob = Guid.NewGuid();
        await LinkAsync(db, alice, 1);
        await LinkAsync(db, bob, 2);
        await using (var context = db.CreateDbContext())
        {
            context.NotificationSettings.Add(new NotificationSettings
            {
                UserId = alice,
                Enabled = !disableAll,
                OnTransferSent = !disableSent,
            });
            await context.SaveChangesAsync();
        }

        await handler.HandleAsync(Transfer(Guid.NewGuid(), alice, bob), default);

        var delivery = Assert.Single(await DeliveriesAsync(db));
        Assert.Equal(2, delivery.ChatId);
    }

    [Fact]
    public async Task ReceivedAndDepositFlags_Suppress()
    {
        var (db, handler) = await SetupAsync();
        var owner = Guid.NewGuid();
        await LinkAsync(db, owner, 1);
        await using (var context = db.CreateDbContext())
        {
            context.NotificationSettings.Add(new NotificationSettings
            {
                UserId = owner, OnTransferReceived = false, OnDeposit = false,
            });
            await context.SaveChangesAsync();
        }

        await handler.HandleAsync(Transfer(Guid.NewGuid(), Guid.NewGuid(), owner), default);
        await handler.HandleAsync(
            Body(Guid.NewGuid(), EventTypes.AccountDeposited, new AccountDeposited(Guid.NewGuid(), Guid.NewGuid(), owner, 100, "RUB")),
            default);

        Assert.Empty(await DeliveriesAsync(db));
    }

    [Fact]
    public async Task DuplicateEvent_DoesNotQueueTwice()
    {
        var (db, handler) = await SetupAsync();
        var owner = Guid.NewGuid();
        await LinkAsync(db, owner, 1);
        var body = Transfer(Guid.NewGuid(), owner, null);

        await handler.HandleAsync(body, default);
        var second = await handler.HandleAsync(body, default);

        Assert.Equal(HandleResult.Duplicate, second);
        Assert.Single(await DeliveriesAsync(db));
    }

    [Fact]
    public async Task FailedQueueing_RollsBackReceivedEvent()
    {
        var (db, handler) = await SetupAsync();
        var owner = Guid.NewGuid();
        await LinkAsync(db, owner, 1);
        await using (var context = db.CreateDbContext())
            await context.Database.ExecuteSqlRawAsync("DROP TABLE notifications.telegram_deliveries CASCADE");

        await Assert.ThrowsAnyAsync<Exception>(() => handler.HandleAsync(Transfer(Guid.NewGuid(), owner, null), default));

        await using var check = db.CreateDbContext();
        Assert.Equal(0, await check.ReceivedEvents.CountAsync());
        Assert.Equal(0, await check.ProcessedEvents.CountAsync());
    }
}
