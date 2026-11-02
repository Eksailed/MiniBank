using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using MiniBank.Notifications.Linking;
using MiniBank.Notifications.Telegram;
using MiniBank.TestSupport;

namespace MiniBank.Notifications.Tests;

[Collection("postgres")]
public class BotPollerTests(PostgresFixture postgres)
{
    private sealed class Factory(string connectionString) : IDbContextFactory<NotificationsDbContext>
    {
        public NotificationsDbContext CreateDbContext() => new(NotificationsDbContext.CreateOptions(connectionString));
    }

    private sealed class FakeTelegram : ITelegramClient
    {
        public Queue<Func<IReadOnlyList<TelegramUpdate>>> Batches { get; } = new();
        public List<long> Offsets { get; } = [];
        public List<(long ChatId, string Text)> Replies { get; } = [];

        public Task SendMessageAsync(long chatId, string text, CancellationToken ct)
        {
            lock (Replies) Replies.Add((chatId, text));
            return Task.CompletedTask;
        }

        public async Task<IReadOnlyList<TelegramUpdate>> GetUpdatesAsync(long offset, TimeSpan timeout, CancellationToken ct)
        {
            lock (Offsets) Offsets.Add(offset);
            if (Batches.TryDequeue(out var next))
                return next();
            await Task.Delay(20, ct);
            return [];
        }
    }

    private async Task<(Factory Db, LinkService Links, FakeTelegram Telegram, BotPoller Poller)> SetupAsync()
    {
        var db = new Factory(await postgres.CreateDatabaseAsync());
        await using (var context = db.CreateDbContext())
            await context.Database.MigrateAsync();
        var links = new LinkService(db, TimeProvider.System);
        var telegram = new FakeTelegram();
        var poller = new BotPoller(telegram, links, NullLogger<BotPoller>.Instance) { ErrorPause = TimeSpan.FromMilliseconds(20) };
        return (db, links, telegram, poller);
    }

    [Fact]
    public async Task Start_WithValidCode_LinksChatAndConfirms()
    {
        var (db, links, telegram, poller) = await SetupAsync();
        var user = Guid.NewGuid();
        var code = (await links.CreateCodeAsync(user, default)).Code;
        telegram.Batches.Enqueue(() => [new TelegramUpdate(1, 900, $"/start {code}")]);

        var next = await poller.PollOnceAsync(0, default);

        Assert.Equal(2, next);
        Assert.Equal((900L, "Готово. Уведомления включены."), Assert.Single(telegram.Replies));
        await using var context = db.CreateDbContext();
        Assert.Equal(user, (await context.TelegramLinks.SingleAsync()).UserId);
    }

    [Fact]
    public async Task Start_WithWrongCode_RepliesInvalidAndDoesNotLink()
    {
        var (db, _, telegram, poller) = await SetupAsync();
        telegram.Batches.Enqueue(() => [new TelegramUpdate(5, 900, "/start nope")]);

        await poller.PollOnceAsync(0, default);

        Assert.Equal("Код недействителен или устарел.", Assert.Single(telegram.Replies).Text);
        await using var context = db.CreateDbContext();
        Assert.Empty(await context.TelegramLinks.ToListAsync());
    }

    [Fact]
    public async Task OtherText_GetsHelp_AndNonMessagesAreAcknowledged()
    {
        var (_, _, telegram, poller) = await SetupAsync();
        telegram.Batches.Enqueue(() => [new TelegramUpdate(7, 900, "привет"), new TelegramUpdate(8, null, null)]);

        var next = await poller.PollOnceAsync(0, default);

        Assert.Equal(9, next);
        Assert.Contains("Привязать Telegram", Assert.Single(telegram.Replies).Text);
    }

    [Fact]
    public async Task Loop_SurvivesTelegramErrorsAndAdvancesOffset()
    {
        var (_, _, telegram, poller) = await SetupAsync();
        telegram.Batches.Enqueue(() => throw new TelegramUnavailableException("down"));
        telegram.Batches.Enqueue(() => [new TelegramUpdate(41, 900, "hi")]);

        await poller.StartAsync(default);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline && !telegram.Offsets.Contains(42))
            await Task.Delay(20);
        await poller.StopAsync(default);

        Assert.Contains(42, telegram.Offsets);
        Assert.Single(telegram.Replies);
    }
}
