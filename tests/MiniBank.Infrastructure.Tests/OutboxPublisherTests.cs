using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using MiniBank.Contracts;
using MiniBank.Infrastructure.Events;
using MiniBank.Infrastructure.Persistence;
using MiniBank.Messaging;
using MiniBank.TestSupport;

namespace MiniBank.Infrastructure.Tests;

[Collection("postgres")]
public class OutboxPublisherTests(PostgresFixture postgres, RabbitMqFixture rabbit) : IClassFixture<RabbitMqFixture>
{
    private static readonly DateTimeOffset Now = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);

    private sealed class FakeSink(Func<OutboxMessage, CancellationToken, Task>? onPublish = null) : IOutboxSink
    {
        public List<Guid> Published { get; } = [];

        public async Task PublishAsync(OutboxMessage message, CancellationToken ct)
        {
            if (onPublish is not null)
                await onPublish(message, ct);
            lock (Published) Published.Add(message.Id);
        }
    }

    private static EventsOptions Options(int maxAttempts = 10, int batchSize = 50, int retentionDays = 7) =>
        new() { MaxAttempts = maxAttempts, BatchSize = batchSize, RetentionDays = retentionDays };

    private async Task<string> DatabaseAsync()
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        await using var db = NewContext(connectionString);
        await db.Database.MigrateAsync();
        return connectionString;
    }

    private static MiniBankDbContext NewContext(string connectionString) =>
        new(new DbContextOptionsBuilder<MiniBankDbContext>().UseNpgsql(connectionString).Options);

    private static OutboxBatchProcessor Processor(MiniBankDbContext db, EventsOptions options, TimeProvider clock) =>
        new(db, options, clock, NullLogger<OutboxBatchProcessor>.Instance);

    private static async Task<List<Guid>> SeedAsync(string connectionString, int count, DateTimeOffset? createdAt = null)
    {
        await using var db = NewContext(connectionString);
        var ids = new List<Guid>();
        for (var i = 0; i < count; i++)
        {
            var id = Guid.NewGuid();
            ids.Add(id);
            db.OutboxMessages.Add(new OutboxMessage
            {
                Id = id,
                Type = EventTypes.AccountDeposited,
                Version = 1,
                Payload = JsonSerializer.Serialize(
                    new AccountDeposited(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 100, "RUB"), EventJson.Options),
                CreatedAt = (createdAt ?? Now).AddMilliseconds(i),
                OccurredAt = createdAt ?? Now,
                NextAttemptAt = createdAt ?? Now,
            });
        }

        await db.SaveChangesAsync();
        return ids;
    }

    private static async Task<List<OutboxMessage>> RowsAsync(string connectionString)
    {
        await using var db = NewContext(connectionString);
        return await db.OutboxMessages.AsNoTracking().OrderBy(m => m.CreatedAt).ToListAsync();
    }

    [Fact]
    public async Task PublishesPendingMessagesAndMarksThem()
    {
        await rabbit.ResetAsync();
        var connectionString = await DatabaseAsync();
        var ids = await SeedAsync(connectionString, 3);
        await using var sink = new RabbitMqOutboxSink(rabbit.Options);

        await using (var db = NewContext(connectionString))
            Assert.Equal(3, await Processor(db, Options(), new FakeClock(Now)).ProcessBatchAsync(sink, default));

        var rows = await RowsAsync(connectionString);
        Assert.All(rows, r => Assert.NotNull(r.PublishedAt));
        var messages = await rabbit.TakeAsync(RabbitMqTopology.Queue, 3);
        Assert.Equal(3, messages.Count);
        Assert.Equal(ids.Select(i => i.ToString()).Order(), messages.Select(m => m.MessageId!).Order());
        Assert.All(messages, m => Assert.Equal(EventTypes.AccountDeposited, m.RoutingKey));

        var envelope = JsonDocument.Parse(messages[0].Body).RootElement;
        Assert.Equal(messages[0].MessageId, envelope.GetProperty("eventId").GetString());
        Assert.Equal(EventTypes.AccountDeposited, envelope.GetProperty("type").GetString());
        Assert.Equal(1, envelope.GetProperty("version").GetInt32());
        Assert.Equal(Now, envelope.GetProperty("occurredAt").GetDateTimeOffset());
        Assert.Equal(100, envelope.GetProperty("data").GetProperty("amount").GetInt64());
    }

    [Fact]
    public async Task BrokerDown_KeepsRowsAndDeliversAfterBrokerReturns()
    {
        await rabbit.ResetAsync();
        var connectionString = await DatabaseAsync();
        await SeedAsync(connectionString, 2);
        var clock = new FakeClock(Now);

        await using (var down = new RabbitMqOutboxSink(RabbitMqFixture.Unreachable))
        await using (var db = NewContext(connectionString))
            await Processor(db, Options(), clock).ProcessBatchAsync(down, default);

        var failed = await RowsAsync(connectionString);
        Assert.All(failed, r =>
        {
            Assert.Null(r.PublishedAt);
            Assert.Null(r.DeadAt);
            Assert.Equal(0, r.Attempts);
        });

        clock.Advance(TimeSpan.FromMinutes(1));
        await using (var up = new RabbitMqOutboxSink(rabbit.Options))
        await using (var db = NewContext(connectionString))
            await Processor(db, Options(), clock).ProcessBatchAsync(up, default);

        Assert.All(await RowsAsync(connectionString), r => Assert.NotNull(r.PublishedAt));
        Assert.Equal(2, (await rabbit.TakeAsync(RabbitMqTopology.Queue, 2)).Count);
    }

    [Fact]
    public async Task BrokerUnavailable_AbortsBatchWithoutBurningAttemptsOrKillingRows()
    {
        var connectionString = await DatabaseAsync();
        var ids = await SeedAsync(connectionString, 3);
        var clock = new FakeClock(Now);
        var calls = 0;
        var sink = new FakeSink((m, _) =>
        {
            calls++;
            return m.Id == ids[0] ? Task.CompletedTask : throw new BrokerUnavailableException("брокер лежит", new IOException());
        });

        // Простой дольше, чем набирается MaxAttempts: строки не должны стать «мёртвыми».
        for (var i = 0; i < 15; i++)
        {
            await using var db = NewContext(connectionString);
            await Processor(db, Options(maxAttempts: 3), clock).ProcessBatchAsync(sink, default);
            clock.Advance(TimeSpan.FromMinutes(10));
        }

        var rows = (await RowsAsync(connectionString)).ToDictionary(r => r.Id);
        Assert.NotNull(rows[ids[0]].PublishedAt);
        Assert.All(new[] { ids[1], ids[2] }, id =>
        {
            Assert.Null(rows[id].DeadAt);
            Assert.Null(rows[id].PublishedAt);
            Assert.Equal(0, rows[id].Attempts);
        });
        Assert.Equal(1 + 15, calls); // на каждом проходе пробуем только первую неотправленную строку
    }

    [Fact]
    public async Task PublishFailure_IncrementsAttemptsAndPostponesRetryExponentially()
    {
        var connectionString = await DatabaseAsync();
        await SeedAsync(connectionString, 1);
        var clock = new FakeClock(Now);
        var sink = new FakeSink((_, _) => throw new InvalidOperationException("boom " + new string('x', 5000)));

        await using (var db = NewContext(connectionString))
            Assert.Equal(1, await Processor(db, Options(), clock).ProcessBatchAsync(sink, default));
        var first = Assert.Single(await RowsAsync(connectionString));
        Assert.Equal(1, first.Attempts);
        Assert.Equal(Now.AddSeconds(5), first.NextAttemptAt);
        Assert.StartsWith("InvalidOperationException: boom", first.LastError);
        Assert.True(first.LastError!.Length <= 2000);

        // До срока строка не выбирается.
        await using (var db = NewContext(connectionString))
            Assert.Equal(0, await Processor(db, Options(), clock).ProcessBatchAsync(sink, default));

        clock.Advance(TimeSpan.FromSeconds(5));
        await using (var db = NewContext(connectionString))
            await Processor(db, Options(), clock).ProcessBatchAsync(sink, default);
        var second = Assert.Single(await RowsAsync(connectionString));
        Assert.Equal(2, second.Attempts);
        Assert.Equal(clock.Now.AddSeconds(10), second.NextAttemptAt);
    }

    [Fact]
    public async Task ExceedingMaxAttempts_SetsDeadAtAndPoisonRowDoesNotBlockOthers()
    {
        var connectionString = await DatabaseAsync();
        var ids = await SeedAsync(connectionString, 3);
        var poison = ids[1];
        var clock = new FakeClock(Now);
        var sink = new FakeSink((m, _) => m.Id == poison ? throw new InvalidOperationException("bad") : Task.CompletedTask);

        for (var i = 0; i < 3; i++)
        {
            await using var db = NewContext(connectionString);
            await Processor(db, Options(maxAttempts: 3), clock).ProcessBatchAsync(sink, default);
            clock.Advance(TimeSpan.FromMinutes(10));
        }

        var rows = (await RowsAsync(connectionString)).ToDictionary(r => r.Id);
        Assert.NotNull(rows[poison].DeadAt);
        Assert.Equal(3, rows[poison].Attempts);
        Assert.Null(rows[poison].PublishedAt);
        Assert.NotNull(rows[ids[0]].PublishedAt);
        Assert.NotNull(rows[ids[2]].PublishedAt);

        await using (var db = NewContext(connectionString))
            Assert.Equal(0, await Processor(db, Options(maxAttempts: 3), clock).ProcessBatchAsync(sink, default));
        Assert.Equal(2, sink.Published.Count);
    }

    [Fact]
    public async Task ConcurrentProcessors_DoNotPublishSameRowTwice()
    {
        var connectionString = await DatabaseAsync();
        var ids = await SeedAsync(connectionString, 100);
        var sink = new FakeSink(async (_, ct) => await Task.Delay(20, ct));

        async Task RunAsync()
        {
            while (true)
            {
                await using var db = NewContext(connectionString);
                if (await Processor(db, Options(batchSize: 10), new FakeClock(Now)).ProcessBatchAsync(sink, default) == 0)
                    return;
            }
        }

        await Task.WhenAll(RunAsync(), RunAsync());

        Assert.Equal(100, sink.Published.Count);
        Assert.Equal(ids.Order(), sink.Published.Order());
        Assert.All(await RowsAsync(connectionString), r => Assert.NotNull(r.PublishedAt));
    }

    [Fact]
    public async Task CleanupDeletesOnlyOldPublishedRows()
    {
        var connectionString = await DatabaseAsync();
        var ids = await SeedAsync(connectionString, 4);
        await using (var db = NewContext(connectionString))
        {
            var rows = await db.OutboxMessages.OrderBy(m => m.CreatedAt).ToListAsync();
            rows[0].PublishedAt = Now.AddDays(-8); // старая опубликованная: удалить
            rows[1].PublishedAt = Now.AddDays(-1); // свежая опубликованная: оставить
            rows[2].DeadAt = Now.AddDays(-30);     // мёртвая: оставить для разбора
            await db.SaveChangesAsync();           // rows[3] не отправлена: оставить
        }

        await using (var db = NewContext(connectionString))
            Assert.Equal(1, await Processor(db, Options(retentionDays: 7), new FakeClock(Now)).CleanupAsync(default));

        Assert.Equal(ids.Skip(1).Order(), (await RowsAsync(connectionString)).Select(r => r.Id).Order());
    }

    [Fact]
    public async Task CrashBetweenBrokerConfirmAndCommit_ResendsRow()
    {
        var connectionString = await DatabaseAsync();
        var ids = await SeedAsync(connectionString, 1);
        using var cts = new CancellationTokenSource();
        // Брокер «подтвердил», и процесс умирает до коммита транзакции.
        var crashing = new FakeSink((_, _) =>
        {
            cts.Cancel();
            return Task.CompletedTask;
        });

        await using (var db = NewContext(connectionString))
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => Processor(db, Options(), new FakeClock(Now)).ProcessBatchAsync(crashing, cts.Token));

        Assert.Null(Assert.Single(await RowsAsync(connectionString)).PublishedAt);

        var healthy = new FakeSink();
        await using (var db = NewContext(connectionString))
            await Processor(db, Options(), new FakeClock(Now)).ProcessBatchAsync(healthy, default);

        Assert.Equal(ids, crashing.Published.Concat(healthy.Published).Distinct());
        Assert.Equal(ids, healthy.Published);
        Assert.NotNull(Assert.Single(await RowsAsync(connectionString)).PublishedAt);
    }

    [Fact]
    public async Task OldestPendingAge_ReportsAgeOfOldestUnpublishedRow()
    {
        var connectionString = await DatabaseAsync();
        await using (var db = NewContext(connectionString))
            Assert.Null(await Processor(db, Options(), new FakeClock(Now)).OldestPendingAgeAsync(default));

        await SeedAsync(connectionString, 1, createdAt: Now.AddMinutes(-7));

        await using (var db = NewContext(connectionString))
            Assert.Equal(TimeSpan.FromMinutes(7), await Processor(db, Options(), new FakeClock(Now)).OldestPendingAgeAsync(default));
    }

    private static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.ToDictionary(v => v.Key, v => (string?)v.Value)).Build();

    [Fact]
    public void Disabled_DoesNotRegisterWorker()
    {
        var services = new ServiceCollection();

        services.AddOutboxPublisher(Config(("Events:Enabled", "false")));

        Assert.DoesNotContain(services, d => d.ServiceType == typeof(IHostedService));
    }

    [Fact]
    public void Enabled_RegistersWorker()
    {
        var services = new ServiceCollection();

        services.AddOutboxPublisher(Config(("RabbitMq:Host", "rabbit")));

        Assert.Contains(services, d => d.ServiceType == typeof(IHostedService));
    }

    [Theory]
    [InlineData("Events:BatchSize", "0")]
    [InlineData("Events:MaxAttempts", "0")]
    [InlineData("Events:RetentionDays", "0")]
    [InlineData("Events:PollInterval", "00:00:00")]
    [InlineData("Events:StaleAfter", "-00:01:00")]
    [InlineData("RabbitMq:Port", "0")]
    public void InvalidOptions_FailOnStartup(string key, string value)
    {
        var services = new ServiceCollection();

        Assert.Throws<InvalidOperationException>(() => services.AddOutboxPublisher(Config((key, value))));
    }
}
