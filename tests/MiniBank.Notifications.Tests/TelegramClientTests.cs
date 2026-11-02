using System.Net;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MiniBank.Notifications.Telegram;

namespace MiniBank.Notifications.Tests;

public class TelegramClientTests
{
    private const string Token = "123456:SECRET-TOKEN";

    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public List<(string Path, string Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            Requests.Add((request.RequestUri!.AbsolutePath, body));
            return await respond(request);
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static (TelegramClient Client, StubHandler Handler) Create(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond)
    {
        var handler = new StubHandler(respond);
        var options = new TelegramOptions { BotToken = Token, BotUsername = "bot", InternalSecret = "0123456789abcdef" };
        return (new TelegramClient(new HttpClient(handler), options), handler);
    }

    private sealed class CapturingLogger(List<string> lines) : ILoggerProvider, ILogger
    {
        public ILogger CreateLogger(string categoryName) => this;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (lines) lines.Add(formatter(state, exception) + exception);
        }
        public void Dispose() { }
    }

    [Fact]
    public async Task ClientFromContainer_DoesNotLogTokenInRequestUrl()
    {
        var lines = new List<string>();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Telegram:BotToken"] = Token,
            ["Telegram:BotUsername"] = "bot",
            ["Telegram:ApiBaseUrl"] = "http://127.0.0.1:1",
            ["Notifications:InternalSecret"] = "0123456789abcdef",
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(new CapturingLogger(lines)));
        services.AddTelegram(configuration);
        await using var provider = services.BuildServiceProvider();

        try { await provider.GetRequiredService<ITelegramClient>().SendMessageAsync(1, "hi", default); }
        catch (Exception) { /* порт закрыт: важны только записи в журнале */ }

        Assert.DoesNotContain(lines, l => l.Contains("SECRET-TOKEN"));
    }

    [Fact]
    public async Task SendMessage_PostsChatIdAndText()
    {
        var (client, handler) = Create(_ => Task.FromResult(Json(HttpStatusCode.OK, """{"ok":true,"result":{}}""")));

        await client.SendMessageAsync(42, "Привет", default);

        var request = Assert.Single(handler.Requests);
        Assert.Equal($"/bot{Token}/sendMessage", request.Path);
        Assert.Contains("\"chat_id\":42", request.Body);
        using var json = System.Text.Json.JsonDocument.Parse(request.Body);
        Assert.Equal("Привет", json.RootElement.GetProperty("text").GetString());
    }

    [Fact]
    public async Task SendMessage_429_ThrowsRateLimitedWithRetryAfter()
    {
        var (client, _) = Create(_ => Task.FromResult(Json(HttpStatusCode.TooManyRequests,
            """{"ok":false,"error_code":429,"parameters":{"retry_after":7}}""")));

        var ex = await Assert.ThrowsAsync<TelegramRateLimitedException>(() => client.SendMessageAsync(1, "x", default));

        Assert.Equal(TimeSpan.FromSeconds(7), ex.RetryAfter);
    }

    [Fact]
    public async Task SendMessage_403_ThrowsForbidden()
    {
        var (client, _) = Create(_ => Task.FromResult(Json(HttpStatusCode.Forbidden, """{"ok":false,"error_code":403}""")));
        await Assert.ThrowsAsync<TelegramForbiddenException>(() => client.SendMessageAsync(1, "x", default));
    }

    [Fact]
    public async Task SendMessage_400_ThrowsRejected()
    {
        var (client, _) = Create(_ => Task.FromResult(Json(HttpStatusCode.BadRequest, """{"ok":false,"error_code":400}""")));
        await Assert.ThrowsAsync<TelegramRejectedException>(() => client.SendMessageAsync(1, "x", default));
    }

    [Fact]
    public async Task SendMessage_500_ThrowsUnavailable()
    {
        var (client, _) = Create(_ => Task.FromResult(Json(HttpStatusCode.InternalServerError, "{}")));
        await Assert.ThrowsAsync<TelegramUnavailableException>(() => client.SendMessageAsync(1, "x", default));
    }

    [Fact]
    public async Task SendMessage_NetworkError_ThrowsUnavailableWithoutToken()
    {
        var (client, _) = Create(_ => throw new HttpRequestException($"connection to https://api.telegram.org/bot{Token}/sendMessage refused"));

        var ex = await Assert.ThrowsAsync<TelegramUnavailableException>(() => client.SendMessageAsync(1, "x", default));

        Assert.DoesNotContain(Token, ex.ToString());
        Assert.DoesNotContain("SECRET-TOKEN", ex.ToString());
    }

    [Fact]
    public async Task SendMessage_ErrorMessages_DoNotContainToken()
    {
        var (client, _) = Create(_ => Task.FromResult(Json(HttpStatusCode.BadRequest, $$"""{"ok":false,"description":"bad {{Token}}"}""")));

        var ex = await Assert.ThrowsAsync<TelegramRejectedException>(() => client.SendMessageAsync(1, "x", default));

        Assert.DoesNotContain("SECRET-TOKEN", ex.ToString());
    }

    [Fact]
    public async Task GetUpdates_ParsesMessagesAndSkipsOthers()
    {
        var (client, handler) = Create(_ => Task.FromResult(Json(HttpStatusCode.OK, """
            {"ok":true,"result":[
              {"update_id":10,"message":{"chat":{"id":555},"text":"/start abc"}},
              {"update_id":11,"edited_channel_post":{}},
              {"update_id":12,"message":{"chat":{"id":556}}}
            ]}
            """)));

        var updates = await client.GetUpdatesAsync(10, TimeSpan.FromSeconds(25), default);

        Assert.Equal(new[] { new TelegramUpdate(10, 555, "/start abc"), new TelegramUpdate(11, null, null), new TelegramUpdate(12, 556, null) }, updates);
        var request = Assert.Single(handler.Requests);
        Assert.Equal($"/bot{Token}/getUpdates", request.Path);
        Assert.Contains("\"offset\":10", request.Body);
        Assert.Contains("\"timeout\":25", request.Body);
    }
}
