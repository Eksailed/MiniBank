using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MiniBank.Notifications.Internal;
using MiniBank.Notifications.Linking;
using MiniBank.Notifications.Telegram;
using MiniBank.TestSupport;

namespace MiniBank.Notifications.Tests;

[Collection("postgres")]
public class InternalApiTests(PostgresFixture postgres)
{
    private const string Secret = "0123456789abcdef-secret";
    private const string Token = "123456:BOT-TOKEN";

    private sealed class Factory(string connectionString) : IDbContextFactory<NotificationsDbContext>
    {
        public NotificationsDbContext CreateDbContext() => new(NotificationsDbContext.CreateOptions(connectionString));
    }

    private sealed class Running(WebApplication app, HttpClient client, LinkService links) : IAsyncDisposable
    {
        public HttpClient Client { get; } = client;
        public LinkService Links { get; } = links;

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    private async Task<Running> StartAsync()
    {
        var db = new Factory(await postgres.CreateDatabaseAsync());
        await using (var context = db.CreateDbContext())
            await context.Database.MigrateAsync();

        var options = new TelegramOptions { BotToken = Token, BotUsername = "mybot", InternalSecret = Secret };
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton<IDbContextFactory<NotificationsDbContext>>(db);
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
        builder.Services.AddSingleton<LinkService>();
        var app = builder.Build();
        app.MapInternalApi();
        await app.StartAsync();

        var client = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
        return new Running(app, client, app.Services.GetRequiredService<LinkService>());
    }

    private static HttpRequestMessage Request(HttpMethod method, string path, string? secret = Secret, object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (secret is not null)
            request.Headers.Add("X-Internal-Secret", secret);
        if (body is not null)
            request.Content = JsonContent.Create(body);
        return request;
    }

    public static TheoryData<string, string> Routes => new()
    {
        { "GET", "/internal/users/{0}/telegram" },
        { "POST", "/internal/users/{0}/telegram/link-code" },
        { "DELETE", "/internal/users/{0}/telegram" },
        { "PUT", "/internal/users/{0}/telegram/settings" },
    };

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task WithoutOrWithWrongSecret_Returns401(string method, string template)
    {
        await using var api = await StartAsync();
        var path = string.Format(template, Guid.NewGuid());

        var missing = await api.Client.SendAsync(Request(new HttpMethod(method), path, secret: null));
        var wrong = await api.Client.SendAsync(Request(new HttpMethod(method), path, secret: "wrong-secret-value-0000"));

        Assert.Equal(HttpStatusCode.Unauthorized, missing.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.DoesNotContain(Secret, await wrong.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task FullCycle_LinkSettingsUnlink()
    {
        await using var api = await StartAsync();
        var user = Guid.NewGuid();

        var codeResponse = await api.Client.SendAsync(Request(HttpMethod.Post, $"/internal/users/{user}/telegram/link-code"));
        codeResponse.EnsureSuccessStatusCode();
        var codeBody = await codeResponse.Content.ReadAsStringAsync();
        using var codeJson = JsonDocument.Parse(codeBody);
        var url = codeJson.RootElement.GetProperty("url").GetString()!;
        Assert.StartsWith("https://t.me/mybot?start=", url);
        Assert.True(codeJson.RootElement.TryGetProperty("expiresAt", out _));
        Assert.DoesNotContain(Secret, codeBody);
        Assert.DoesNotContain(Token, codeBody);

        var before = await GetAsync(api, user);
        Assert.False(before.GetProperty("linked").GetBoolean());

        Assert.True(await api.Links.RedeemAsync(url["https://t.me/mybot?start=".Length..], 321, default));
        var put = await api.Client.SendAsync(Request(HttpMethod.Put, $"/internal/users/{user}/telegram/settings",
            body: new { enabled = true, onTransferSent = false, onTransferReceived = true, onDeposit = true }));
        Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);

        var after = await GetAsync(api, user);
        Assert.True(after.GetProperty("linked").GetBoolean());
        Assert.False(after.GetProperty("settings").GetProperty("onTransferSent").GetBoolean());

        var delete = await api.Client.SendAsync(Request(HttpMethod.Delete, $"/internal/users/{user}/telegram"));
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        Assert.False((await GetAsync(api, user)).GetProperty("linked").GetBoolean());
    }


    private static async Task<JsonElement> GetAsync(Running api, Guid user)
    {
        var response = await api.Client.SendAsync(Request(HttpMethod.Get, $"/internal/users/{user}/telegram"));
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }
}
