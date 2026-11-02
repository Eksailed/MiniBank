using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using MiniBank.Notifications.Telegram;
using MiniBank.TestSupport;

namespace MiniBank.Notifications.Tests;

[Collection("postgres")]
public class DeliverySenderTests(PostgresFixture postgres)
{
    private static readonly DateTimeOffset Start = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);

    private sealed class Factory(string connectionString) : IDbContextFactory<NotificationsDbContext>
    {
        public NotificationsDbContext CreateDbContext() => new(NotificationsDbContext.CreateOptions(connectionString));
    }

    private sealed class ScriptedTelegram : ITelegramClient
    {
        public Func<long, string, Exception?> Behavior { get; set; } = (_, _) => null;
        public List<(long ChatId, string Text)> Sent { get; } = [];

        public Task SendMessageAsync(long chatId, string text, CancellationToken ct)
        {
            if (Behavior(chatId, text) is { } error)
                throw error;
            Sent.Add((chatId, text));
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<TelegramUpdate>> GetUpdatesAsync(long offset, TimeSpan timeout, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<TelegramUpdate>>([]);
    }

    private sealed record Rig(Factory Db, FakeClock Clock, ScriptedTelegram Telegram, DeliveryBatchProcessor Processor);

    private async Task<Rig> SetupAsync(int maxAttempts = 3)
    {
        var db = new Factory(await postgres.CreateDatabaseAsync());
        await using (var context = db.CreateDbContext())
            await context.Database.MigrateAsync();
        var clock = new FakeClock(Start);
        var telegram = new ScriptedTelegram();
        var options = new TelegramOptions
        {
            BotToken = "123456:SECRET-TOKEN", BotUsername = "bot", InternalSecret = "0123456789abcdef",
            DeliveryMaxAttempts = maxAttempts, DeliveryBatchSize = 20,
        };
        return new Rig(db, clock, telegram,
            new DeliveryBatchProcessor(db, options, clock, telegram, NullLogger<DeliveryBatchProcessor>.Instance));
    }

    private static async Task<TelegramDelivery> AddDeliveryAsync(Rig rig, long chatId, string text = "Привет", DateTimeOffset? sentAt = null,
        DeliveryStatus status = DeliveryStatus.Pending)
    {
        await using var context = rig.Db.CreateDbContext();
        var received = new ReceivedEvent
        {
            Id = Guid.NewGuid(), EventId = Guid.NewGuid(), Type = "transfer.completed", OwnerId = Guid.NewGuid(),
            OccurredAt = Start, ReceivedAt = Start,
        };
        var delivery = new TelegramDelivery
        {
            Id = Guid.NewGuid(), ReceivedEventId = received.Id, ChatId = chatId, Text = text, Status = status,
            NextAttemptAt = Start, SentAt = sentAt,
        };
        context.ReceivedEvents.Add(received);
        context.TelegramDeliveries.Add(delivery);
        await context.SaveChangesAsync();
        return delivery;
    }

    private static async Task<TelegramDelivery> ReloadAsync(Rig rig, Guid id)
    {
        await using var context = rig.Db.CreateDbContext();
        return await context.TelegramDeliveries.SingleAsync(d => d.Id == id);
    }

    [Fact]
    public async Task Success_MarksSent()
    {
        var rig = await SetupAsync();
        var delivery = await AddDeliveryAsync(rig, 5, "Перевод: −1,00 ₽");

        var processed = await rig.Processor.ProcessBatchAsync(default);

        Assert.Equal(1, processed);
        var row = await ReloadAsync(rig, delivery.Id);
        Assert.Equal(DeliveryStatus.Sent, row.Status);
        Assert.Equal(Start, row.SentAt);
        Assert.Equal((5L, "Перевод: −1,00 ₽"), Assert.Single(rig.Telegram.Sent));
    }

    [Fact]
    public async Task RateLimited_WaitsForRetryAfterAndSpendsAttempt()
    {
        var rig = await SetupAsync();
        var delivery = await AddDeliveryAsync(rig, 5);
        rig.Telegram.Behavior = (_, _) => new TelegramRateLimitedException(TimeSpan.FromSeconds(30));

        await rig.Processor.ProcessBatchAsync(default);

        var row = await ReloadAsync(rig, delivery.Id);
        Assert.Equal(DeliveryStatus.Pending, row.Status);
        Assert.Equal(1, row.Attempts);
        Assert.Equal(Start.AddSeconds(30), row.NextAttemptAt);
    }

    [Fact]
    public async Task Rejected_BacksOffExponentiallyThenFails()
    {
        var rig = await SetupAsync(maxAttempts: 3);
        var delivery = await AddDeliveryAsync(rig, 5);
        rig.Telegram.Behavior = (_, _) => new TelegramRejectedException("HTTP 400");

        await rig.Processor.ProcessBatchAsync(default);
        var first = await ReloadAsync(rig, delivery.Id);
        Assert.Equal(Start.AddSeconds(5), first.NextAttemptAt);

        rig.Clock.Advance(TimeSpan.FromSeconds(5));
        await rig.Processor.ProcessBatchAsync(default);
        var second = await ReloadAsync(rig, delivery.Id);
        Assert.Equal(rig.Clock.Now.AddSeconds(10), second.NextAttemptAt);

        rig.Clock.Advance(TimeSpan.FromSeconds(10));
        await rig.Processor.ProcessBatchAsync(default);
        var third = await ReloadAsync(rig, delivery.Id);
        Assert.Equal(DeliveryStatus.Failed, third.Status);
        Assert.Equal(3, third.Attempts);
    }

    [Fact]
    public void Backoff_DoublesAndCapsAtFiveMinutes()
    {
        Assert.Equal(TimeSpan.FromSeconds(5), DeliveryBatchProcessor.Backoff(1));
        Assert.Equal(TimeSpan.FromSeconds(20), DeliveryBatchProcessor.Backoff(3));
        Assert.Equal(TimeSpan.FromMinutes(5), DeliveryBatchProcessor.Backoff(10));
        Assert.Equal(TimeSpan.FromMinutes(5), DeliveryBatchProcessor.Backoff(60));
    }

    [Fact]
    public async Task Unavailable_DoesNotSpendAttemptsAndRecovers()
    {
        var rig = await SetupAsync(maxAttempts: 3);
        var delivery = await AddDeliveryAsync(rig, 5);
        rig.Telegram.Behavior = (_, _) => new TelegramUnavailableException("network");

        for (var i = 0; i < 15; i++)
            await rig.Processor.ProcessBatchAsync(default);

        var stuck = await ReloadAsync(rig, delivery.Id);
        Assert.Equal(DeliveryStatus.Pending, stuck.Status);
        Assert.Equal(0, stuck.Attempts);

        rig.Telegram.Behavior = (_, _) => null;
        await rig.Processor.ProcessBatchAsync(default);

        Assert.Equal(DeliveryStatus.Sent, (await ReloadAsync(rig, delivery.Id)).Status);
    }

    [Fact]
    public async Task Forbidden_RemovesLinkAndFailsPendingDeliveriesOfChat()
    {
        var rig = await SetupAsync();
        var first = await AddDeliveryAsync(rig, 5);
        var second = await AddDeliveryAsync(rig, 5);
        var other = await AddDeliveryAsync(rig, 6);
        await using (var context = rig.Db.CreateDbContext())
        {
            context.TelegramLinks.Add(new TelegramLink { UserId = Guid.NewGuid(), ChatId = 5, LinkedAt = Start });
            context.TelegramLinks.Add(new TelegramLink { UserId = Guid.NewGuid(), ChatId = 6, LinkedAt = Start });
            await context.SaveChangesAsync();
        }

        rig.Telegram.Behavior = (chat, _) => chat == 5 ? new TelegramForbiddenException() : null;
        await rig.Processor.ProcessBatchAsync(default);

        Assert.Equal(DeliveryStatus.Failed, (await ReloadAsync(rig, first.Id)).Status);
        Assert.Equal(DeliveryStatus.Failed, (await ReloadAsync(rig, second.Id)).Status);
        Assert.Equal(DeliveryStatus.Sent, (await ReloadAsync(rig, other.Id)).Status);
        await using var check = rig.Db.CreateDbContext();
        Assert.Equal(new[] { 6L }, await check.TelegramLinks.Select(l => l.ChatId).ToListAsync());
    }

    [Fact]
    public async Task LastError_IsBoundedAndHasNoToken()
    {
        var rig = await SetupAsync();
        var delivery = await AddDeliveryAsync(rig, 5);
        rig.Telegram.Behavior = (_, _) =>
            new TelegramRejectedException(new string('x', 5_000) + " 123456:SECRET-TOKEN");

        await rig.Processor.ProcessBatchAsync(default);

        var row = await ReloadAsync(rig, delivery.Id);
        Assert.True(row.LastError!.Length <= 2_000);
        Assert.DoesNotContain("SECRET-TOKEN", row.LastError);
    }

    [Fact]
    public async Task Cleanup_RemovesOldSentAndFailedOnly()
    {
        var rig = await SetupAsync();
        var oldSent = await AddDeliveryAsync(rig, 1, status: DeliveryStatus.Sent, sentAt: Start.AddDays(-31));
        var oldFailed = await AddDeliveryAsync(rig, 2, status: DeliveryStatus.Failed);
        var fresh = await AddDeliveryAsync(rig, 3, status: DeliveryStatus.Sent, sentAt: Start.AddDays(-1));
        var pending = await AddDeliveryAsync(rig, 4);
        await using (var context = rig.Db.CreateDbContext())
        {
            await context.TelegramDeliveries.Where(d => d.Id == oldFailed.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(d => d.NextAttemptAt, Start.AddDays(-31)));
        }

        await rig.Processor.CleanupAsync(default);

        await using var check = rig.Db.CreateDbContext();
        var left = await check.TelegramDeliveries.Select(d => d.Id).ToListAsync();
        Assert.Equal(new[] { fresh.Id, pending.Id }.Order(), left.Order());
        Assert.DoesNotContain(oldSent.Id, left);
    }
}
