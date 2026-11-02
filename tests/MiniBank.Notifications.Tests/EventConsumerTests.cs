using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using MiniBank.Contracts;
using MiniBank.Messaging;
using MiniBank.TestSupport;
using RabbitMQ.Client;

namespace MiniBank.Notifications.Tests;

[Collection("postgres")]
public class EventConsumerTests(PostgresFixture postgres, RabbitMqFixture rabbit) : IClassFixture<RabbitMqFixture>
{
    private static readonly DateTimeOffset Occurred = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);

    private sealed class Factory(string connectionString) : IDbContextFactory<NotificationsDbContext>
    {
        public NotificationsDbContext CreateDbContext() =>
            new(NotificationsDbContext.CreateOptions(connectionString));
    }

    private sealed class Running(EventConsumer consumer) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await consumer.StopAsync(CancellationToken.None);
            consumer.Dispose();
        }
    }

    private async Task<(Factory Db, Running Consumer)> StartAsync()
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        var db = new Factory(connectionString);
        await using (var context = db.CreateDbContext())
            await context.Database.MigrateAsync();

        await rabbit.ResetAsync();
        var handler = new EventHandler(db, NullLogger<EventHandler>.Instance);
        var consumer = new EventConsumer(rabbit.Options, handler, NullLogger<EventConsumer>.Instance);
        await consumer.StartAsync(CancellationToken.None);
        return (db, new Running(consumer));
    }

    private static byte[] Body(Guid eventId, string type, object data, int version = 1) =>
        JsonSerializer.SerializeToUtf8Bytes(
            new EventEnvelope(eventId, type, version, Occurred, JsonSerializer.SerializeToElement(data, EventJson.Options)),
            EventJson.Options);

    private static TransferCompleted Transfer(Guid fromOwner, Guid? toOwner) =>
        new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), fromOwner, toOwner, 2_500, "RUB");

    private async Task PublishAsync(byte[] body, Guid? messageId, string routingKey = EventTypes.TransferCompleted)
    {
        await using var connection = await rabbit.ConnectAsync();
        await using var channel = await connection.CreateChannelAsync();
        var properties = new BasicProperties { Persistent = true, MessageId = messageId?.ToString() };
        await channel.BasicPublishAsync(RabbitMqTopology.Exchange, routingKey, mandatory: false, properties, body);
    }

    private static async Task EventuallyAsync(Func<Task<bool>> condition, string because)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            if (await condition())
                return;
            await Task.Delay(100);
        }

        Assert.Fail($"Не дождались: {because}");
    }

    private static Task<int> ProcessedCountAsync(IDbContextFactory<NotificationsDbContext> db) =>
        CountAsync(db, c => c.ProcessedEvents.CountAsync());

    private static async Task<int> CountAsync(IDbContextFactory<NotificationsDbContext> db, Func<NotificationsDbContext, Task<int>> query)
    {
        await using var context = db.CreateDbContext();
        return await query(context);
    }

    private async Task AssertQueuesEmptyAsync()
    {
        await EventuallyAsync(async () => await rabbit.MessageCountAsync(RabbitMqTopology.Queue) == 0, "основная очередь пуста");
        Assert.Equal(0u, await rabbit.MessageCountAsync(RabbitMqTopology.DeadQueue));
    }

    [Fact]
    public async Task DeliveredTwice_RecordsOnce()
    {
        var (db, consumer) = await StartAsync();
        await using var _ = consumer;
        var alice = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        var body = Body(eventId, EventTypes.TransferCompleted, Transfer(alice, null));

        await PublishAsync(body, eventId);
        await PublishAsync(body, eventId);

        await EventuallyAsync(async () => await rabbit.MessageCountAsync(RabbitMqTopology.Queue) == 0, "обе доставки подтверждены");
        await EventuallyAsync(async () => await ProcessedCountAsync(db) == 1, "событие записано");
        Assert.Equal(1, await CountAsync(db, c => c.ReceivedEvents.CountAsync()));
        await AssertQueuesEmptyAsync();
    }

    [Fact]
    public async Task TransferCompleted_RecordsBothOwners()
    {
        var (db, consumer) = await StartAsync();
        await using var _ = consumer;
        var alice = Guid.NewGuid();
        var bob = Guid.NewGuid();
        var eventId = Guid.NewGuid();

        await PublishAsync(Body(eventId, EventTypes.TransferCompleted, Transfer(alice, bob)), eventId);

        await EventuallyAsync(async () => await CountAsync(db, c => c.ReceivedEvents.CountAsync()) == 2, "две записи");
        await using var context = db.CreateDbContext();
        var owners = await context.ReceivedEvents.Where(e => e.EventId == eventId).Select(e => e.OwnerId).ToListAsync();
        Assert.Equivalent(new[] { alice, bob }, owners);
        Assert.All(await context.ReceivedEvents.ToListAsync(), e =>
        {
            Assert.Equal(EventTypes.TransferCompleted, e.Type);
            Assert.Equal(Occurred, e.OccurredAt);
        });
    }

    [Fact]
    public async Task AccountDeposited_RecordsOwner()
    {
        var (db, consumer) = await StartAsync();
        await using var _ = consumer;
        var alice = Guid.NewGuid();
        var eventId = Guid.NewGuid();

        await PublishAsync(
            Body(eventId, EventTypes.AccountDeposited, new AccountDeposited(Guid.NewGuid(), Guid.NewGuid(), alice, 7_000, "RUB")),
            eventId, EventTypes.AccountDeposited);

        await EventuallyAsync(async () => await CountAsync(db, c => c.ReceivedEvents.CountAsync(e => e.OwnerId == alice)) == 1, "запись владельца");
    }

    [Fact]
    public async Task UnknownTypeOrVersion_IsAckedAndIgnored()
    {
        var (db, consumer) = await StartAsync();
        await using var _ = consumer;
        var ignoredType = Guid.NewGuid();
        var ignoredVersion = Guid.NewGuid();
        var valid = Guid.NewGuid();

        await PublishAsync(Body(ignoredType, "x.y", new { a = 1 }), ignoredType, "x.y");
        await PublishAsync(Body(ignoredVersion, EventTypes.TransferCompleted, Transfer(Guid.NewGuid(), null), version: 2), ignoredVersion);
        await PublishAsync(Body(valid, EventTypes.TransferCompleted, Transfer(Guid.NewGuid(), null)), valid);

        await EventuallyAsync(async () => await ProcessedCountAsync(db) == 1, "валидное сообщение обработано");
        await using var context = db.CreateDbContext();
        Assert.Equal(valid, (await context.ProcessedEvents.SingleAsync()).EventId);
        await AssertQueuesEmptyAsync();
    }

    [Fact]
    public async Task MalformedJson_GoesToDeadLetterWithoutBlocking()
    {
        var (db, consumer) = await StartAsync();
        await using var _ = consumer;
        var valid = Guid.NewGuid();

        await PublishAsync("{ это не json"u8.ToArray(), messageId: null);
        await PublishAsync(Body(valid, EventTypes.TransferCompleted, Transfer(Guid.NewGuid(), null)), valid);

        await EventuallyAsync(async () => await ProcessedCountAsync(db) == 1, "валидное сообщение обработано");
        await EventuallyAsync(async () => await rabbit.MessageCountAsync(RabbitMqTopology.DeadQueue) == 1, "битое сообщение в dead-letter");
        Assert.Equal(0u, await rabbit.MessageCountAsync(RabbitMqTopology.Queue));
    }

    [Fact]
    public async Task HandlerFailure_RetriesLimitedTimesThenDeadLetters()
    {
        var (db, consumer) = await StartAsync();
        await using var _ = consumer;
        await using (var context = db.CreateDbContext())
            await context.Database.ExecuteSqlRawAsync("DROP TABLE notifications.received_events");
        var eventId = Guid.NewGuid();

        await PublishAsync(Body(eventId, EventTypes.TransferCompleted, Transfer(Guid.NewGuid(), null)), eventId);

        await EventuallyAsync(async () => await rabbit.MessageCountAsync(RabbitMqTopology.DeadQueue) == 1, "сообщение в dead-letter после повторов");
        var dead = Assert.Single(await rabbit.TakeAsync(RabbitMqTopology.DeadQueue, 1));
        Assert.Equal(eventId.ToString(), dead.MessageId);
        Assert.Equal(0u, await rabbit.MessageCountAsync(RabbitMqTopology.Queue));
        Assert.Equal(0, await ProcessedCountAsync(db));
    }

    [Fact]
    public async Task ProcessedAndReceivedAreAtomic()
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        var db = new Factory(connectionString);
        await using (var context = db.CreateDbContext())
        {
            await context.Database.MigrateAsync();
            await context.Database.ExecuteSqlRawAsync("DROP TABLE notifications.received_events");
        }

        var handler = new EventHandler(db, NullLogger<EventHandler>.Instance);
        var eventId = Guid.NewGuid();

        await Assert.ThrowsAnyAsync<Exception>(() =>
            handler.HandleAsync(Body(eventId, EventTypes.TransferCompleted, Transfer(Guid.NewGuid(), null)), default));

        Assert.Equal(0, await ProcessedCountAsync(db));
    }

    [Fact]
    public async Task Handler_ReportsDuplicateAndIgnored()
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        var db = new Factory(connectionString);
        await using (var context = db.CreateDbContext())
            await context.Database.MigrateAsync();
        var handler = new EventHandler(db, NullLogger<EventHandler>.Instance);
        var eventId = Guid.NewGuid();
        var body = Body(eventId, EventTypes.TransferCompleted, Transfer(Guid.NewGuid(), null));

        Assert.Equal(HandleResult.Processed, await handler.HandleAsync(body, default));
        Assert.Equal(HandleResult.Duplicate, await handler.HandleAsync(body, default));
        Assert.Equal(HandleResult.Ignored, await handler.HandleAsync(Body(Guid.NewGuid(), "x.y", new { }), default));
        await Assert.ThrowsAsync<MalformedEventException>(() => handler.HandleAsync("not json"u8.ToArray(), default));
    }
}
