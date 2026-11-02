using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MiniBank.Application;
using MiniBank.Messaging;
using MiniBank.Notifications;
using MiniBank.Notifications.Internal;
using MiniBank.TestSupport;

namespace MiniBank.Api.IntegrationTests;

/// <summary>
/// Весь путь: API → outbox → RabbitMQ → сервис уведомлений → «Telegram». Сервис уведомлений поднят в процессе теста
/// с настоящими потребителем, отправителем и ботом; Telegram подменён HTTP-сервером.
/// </summary>
[Collection("postgres")]
public class TelegramEndToEndTests(PostgresFixture postgres, RabbitMqFixture rabbit) : IClassFixture<RabbitMqFixture>
{
    private const string Secret = "e2e-internal-secret-0123456789";
    private const string BotToken = "111:E2E-TOKEN";

    private sealed class FakeTelegramServer : IAsyncDisposable
    {
        private readonly WebApplication _app;
        private readonly ConcurrentQueue<JsonElement> _updates = new();

        public ConcurrentQueue<(long ChatId, string Text)> Sent { get; } = new();
        public string Url => _app.Urls.First();

        private FakeTelegramServer(WebApplication app) => _app = app;

        public static async Task<FakeTelegramServer> StartAsync()
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();
            var app = builder.Build();
            var server = new FakeTelegramServer(app);

            app.MapPost($"/bot{BotToken}/getUpdates", async () =>
            {
                if (server._updates.TryDequeue(out var update))
                    return Results.Json(new { ok = true, result = new[] { update } });
                await Task.Delay(100);
                return Results.Json(new { ok = true, result = Array.Empty<object>() });
            });
            app.MapPost($"/bot{BotToken}/sendMessage", async (HttpRequest request) =>
            {
                using var body = await JsonDocument.ParseAsync(request.Body);
                server.Sent.Enqueue((body.RootElement.GetProperty("chat_id").GetInt64(), body.RootElement.GetProperty("text").GetString()!));
                return Results.Json(new { ok = true, result = new { message_id = 1 } });
            });

            await app.StartAsync();
            return server;
        }

        private long _updateId;

        public void UserSends(long chatId, string text)
        {
            var id = Interlocked.Increment(ref _updateId);
            _updates.Enqueue(JsonSerializer.SerializeToElement(new { update_id = id, message = new { chat = new { id = chatId }, text } }));
        }

        public string[] TextsFor(long chatId) => Sent.Where(m => m.ChatId == chatId).Select(m => m.Text).ToArray();

        public async ValueTask DisposeAsync()
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }

    private static async Task<WebApplication> StartNotificationsAsync(
        string connectionString, RabbitMqOptions broker, string telegramUrl)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Notifications"] = connectionString,
            ["RabbitMq:Host"] = broker.Host,
            ["RabbitMq:Port"] = broker.Port.ToString(),
            ["RabbitMq:User"] = broker.User,
            ["RabbitMq:Password"] = broker.Password,
            ["Telegram:BotToken"] = BotToken,
            ["Telegram:BotUsername"] = "e2ebot",
            ["Telegram:ApiBaseUrl"] = telegramUrl,
            ["Notifications:InternalSecret"] = Secret,
            ["Notifications:DeliveryPollInterval"] = "00:00:00.100",
        });
        builder.Services.AddNotifications(builder.Configuration);
        builder.Services.AddTelegram(builder.Configuration);
        var app = builder.Build();
        app.MapInternalApi();
        await app.Services.MigrateNotificationsAsync();
        await app.StartAsync();
        return app;
    }

    private static async Task<T> EventuallyAsync<T>(Func<Task<T>> read, Func<T, bool> done, string what)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        T value = await read();
        while (!done(value))
        {
            Assert.True(DateTime.UtcNow < deadline, $"Не дождались: {what}");
            await Task.Delay(100);
            value = await read();
        }

        return value;
    }

    private async Task<AccountDto> CreateAccountAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/accounts", new { currency = "RUB" });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<AccountDto>())!;
    }

    private async Task LinkAsync(HttpClient client, FakeTelegramServer telegram, long chatId)
    {
        var link = await client.PostAsync("/me/notifications/telegram/link", null);
        link.EnsureSuccessStatusCode();
        var url = (await link.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("url").GetString()!;
        var code = url[(url.IndexOf("start=", StringComparison.Ordinal) + "start=".Length)..];
        telegram.UserSends(chatId, $"/start {code}");

        await EventuallyAsync(
            async () => await client.GetFromJsonAsync<JsonElement>("/me/notifications"),
            status => status.GetProperty("linked").GetBoolean(),
            "привязка чата");
    }

    private sealed record Setup(
        ApiFactory Api, FakeTelegramServer Telegram, WebApplication Notifications, HttpClient Alice, HttpClient Bob,
        AccountDto AliceAccount, AccountDto BobAccount) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            Alice.Dispose();
            Bob.Dispose();
            await Notifications.StopAsync();
            await Notifications.DisposeAsync();
            await Telegram.DisposeAsync();
            await Api.DisposeAsync();
        }
    }

    private async Task<Setup> StartAllAsync()
    {
        await rabbit.ResetAsync();
        var telegram = await FakeTelegramServer.StartAsync();
        var notifications = await StartNotificationsAsync(await postgres.CreateDatabaseAsync(), rabbit.Options, telegram.Url);
        var settings = new Dictionary<string, string?>
        {
            ["Events:Enabled"] = "true",
            ["Events:PollInterval"] = "00:00:00.200",
            ["RabbitMq:Host"] = rabbit.Options.Host,
            ["RabbitMq:Port"] = rabbit.Options.Port.ToString(),
            ["RabbitMq:User"] = rabbit.Options.User,
            ["RabbitMq:Password"] = rabbit.Options.Password,
            ["Notifications:BaseUrl"] = notifications.Urls.First(),
            ["Notifications:InternalSecret"] = Secret,
        };
        var api = await ApiFactory.CreateAsync(postgres, settings: settings);
        var alice = await TestUsers.NewSignedInClientAsync(api, name: "Alice");
        var bob = await TestUsers.NewSignedInClientAsync(api, name: "Bob");
        return new Setup(api, telegram, notifications, alice, bob, await CreateAccountAsync(alice), await CreateAccountAsync(bob));
    }

    [Fact]
    public async Task Transfer_AfterLinking_NotifiesBothParties()
    {
        await using var s = await StartAllAsync();
        await LinkAsync(s.Alice, s.Telegram, chatId: 1001);
        await LinkAsync(s.Bob, s.Telegram, chatId: 2002);

        (await s.Alice.PostWithKeyAsync($"/accounts/{s.AliceAccount.Id}/deposit", new { amount = 10_000, currency = "RUB" }))
            .EnsureSuccessStatusCode();
        (await s.Alice.PostWithKeyAsync("/transfers", new { fromAccountId = s.AliceAccount.Id, toAccountId = s.BobAccount.Id, amount = 2_500, currency = "RUB" }))
            .EnsureSuccessStatusCode();

        await EventuallyAsync(
            () => Task.FromResult((Alice: s.Telegram.TextsFor(1001), Bob: s.Telegram.TextsFor(2002))),
            t => t.Alice.Contains("Перевод: −25,00 ₽") && t.Bob.Contains("Поступление: +25,00 ₽"),
            "уведомления о переводе");
        Assert.Contains("Пополнение: +100,00 ₽", s.Telegram.TextsFor(1001));
    }

    [Fact]
    public async Task DisabledTransferSent_SuppressesOnlyThatMessage()
    {
        await using var s = await StartAllAsync();
        await LinkAsync(s.Alice, s.Telegram, chatId: 1001);
        await LinkAsync(s.Bob, s.Telegram, chatId: 2002);
        (await s.Alice.PutAsJsonAsync("/me/notifications/settings",
            new { enabled = true, onTransferSent = false, onTransferReceived = true, onDeposit = true }))
            .EnsureSuccessStatusCode();

        (await s.Alice.PostWithKeyAsync($"/accounts/{s.AliceAccount.Id}/deposit", new { amount = 10_000, currency = "RUB" }))
            .EnsureSuccessStatusCode();
        (await s.Alice.PostWithKeyAsync("/transfers", new { fromAccountId = s.AliceAccount.Id, toAccountId = s.BobAccount.Id, amount = 2_500, currency = "RUB" }))
            .EnsureSuccessStatusCode();

        await EventuallyAsync(
            () => Task.FromResult(s.Telegram.TextsFor(2002)),
            texts => texts.Contains("Поступление: +25,00 ₽"),
            "поступление у получателя");
        await EventuallyAsync(
            () => Task.FromResult(s.Telegram.TextsFor(1001)),
            texts => texts.Contains("Пополнение: +100,00 ₽"),
            "пополнение у отправителя");
        Assert.DoesNotContain(s.Telegram.TextsFor(1001), t => t.StartsWith("Перевод:", StringComparison.Ordinal));
    }
}
