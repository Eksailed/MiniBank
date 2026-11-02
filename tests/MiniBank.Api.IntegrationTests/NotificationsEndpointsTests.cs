using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MiniBank.Application;
using MiniBank.Application.Abstractions;
using MiniBank.Infrastructure.Notifications;
using MiniBank.TestSupport;

namespace MiniBank.Api.IntegrationTests;

[Collection("postgres")]
public class NotificationsEndpointsTests(PostgresFixture postgres)
{
    private const string Secret = "0123456789abcdef-gateway-secret";

    private sealed class StubGateway : INotificationsGateway
    {
        public List<(string Call, Guid UserId)> Calls { get; } = [];
        public NotificationSettingsDto? LastSettings { get; private set; }
        public Exception? Failure { get; set; }

        private void Record(string call, Guid userId)
        {
            Calls.Add((call, userId));
            if (Failure is not null)
                throw Failure;
        }

        public Task<TelegramStatusDto> GetAsync(Guid userId, CancellationToken ct)
        {
            Record("get", userId);
            return Task.FromResult(new TelegramStatusDto(true, new DateTimeOffset(2026, 11, 2, 9, 0, 0, TimeSpan.Zero),
                new NotificationSettingsDto(true, false, true, true)));
        }

        public Task<LinkDto> CreateLinkAsync(Guid userId, CancellationToken ct)
        {
            Record("link", userId);
            return Task.FromResult(new LinkDto("https://t.me/bot?start=abc", new DateTimeOffset(2026, 11, 2, 9, 10, 0, TimeSpan.Zero)));
        }

        public Task UnlinkAsync(Guid userId, CancellationToken ct)
        {
            Record("unlink", userId);
            return Task.CompletedTask;
        }

        public Task UpdateSettingsAsync(Guid userId, NotificationSettingsDto settings, CancellationToken ct)
        {
            Record("settings", userId);
            LastSettings = settings;
            return Task.CompletedTask;
        }
    }

    private async Task<(ApiFactory Factory, StubGateway Gateway)> CreateAsync()
    {
        var gateway = new StubGateway();
        var factory = await ApiFactory.CreateAsync(postgres, configureServices: services =>
        {
            services.RemoveAll<INotificationsGateway>();
            services.AddSingleton<INotificationsGateway>(gateway);
        });
        return (factory, gateway);
    }

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    private static async Task<Guid> UserIdAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<JsonElement>("/me")).GetProperty("id").GetGuid();

    public static TheoryData<string, string> Routes => new()
    {
        { "GET", "/me/notifications" },
        { "POST", "/me/notifications/telegram/link" },
        { "DELETE", "/me/notifications/telegram" },
        { "PUT", "/me/notifications/settings" },
    };

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task WithoutToken_Returns401(string method, string path)
    {
        var (factory, gateway) = await CreateAsync();
        await using var owner = factory;
        using var client = factory.CreateClient();

        var response = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), path)
        {
            Content = Json("""{"enabled":true,"onTransferSent":true,"onTransferReceived":true,"onDeposit":true}"""),
        });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(gateway.Calls);
    }

    [Fact]
    public async Task Get_ReturnsGatewayStatusForTokenUser()
    {
        var (factory, gateway) = await CreateAsync();
        await using var owner = factory;
        using var client = await TestUsers.NewSignedInClientAsync(factory);

        var body = await client.GetFromJsonAsync<JsonElement>("/me/notifications");

        Assert.True(body.GetProperty("linked").GetBoolean());
        Assert.False(body.GetProperty("settings").GetProperty("onTransferSent").GetBoolean());
        Assert.Equal(("get", await UserIdAsync(client)), Assert.Single(gateway.Calls));
    }

    [Fact]
    public async Task PostLink_ReturnsUrlAndExpiry()
    {
        var (factory, gateway) = await CreateAsync();
        await using var owner = factory;
        using var client = await TestUsers.NewSignedInClientAsync(factory);

        var response = await client.PostAsync("/me/notifications/telegram/link", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("https://t.me/bot?start=abc", body.GetProperty("url").GetString());
        Assert.True(body.TryGetProperty("expiresAt", out _));
        Assert.Equal(("link", await UserIdAsync(client)), Assert.Single(gateway.Calls));
    }

    [Fact]
    public async Task Delete_UnlinksTokenUser()
    {
        var (factory, gateway) = await CreateAsync();
        await using var owner = factory;
        using var client = await TestUsers.NewSignedInClientAsync(factory);

        var response = await client.DeleteAsync("/me/notifications/telegram");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(("unlink", await UserIdAsync(client)), Assert.Single(gateway.Calls));
    }

    [Fact]
    public async Task PutSettings_PassesFourFlagsForTokenUser()
    {
        var (factory, gateway) = await CreateAsync();
        await using var owner = factory;
        using var client = await TestUsers.NewSignedInClientAsync(factory);

        var response = await client.PutAsync("/me/notifications/settings",
            Json("""{"enabled":true,"onTransferSent":false,"onTransferReceived":true,"onDeposit":false}"""));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(("settings", await UserIdAsync(client)), Assert.Single(gateway.Calls));
        Assert.Equal(new NotificationSettingsDto(true, false, true, false), gateway.LastSettings);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("""{"enabled":true,"onTransferSent":true,"onTransferReceived":true}""")]
    [InlineData("""{"enabled":null,"onTransferSent":true,"onTransferReceived":true,"onDeposit":true}""")]
    [InlineData("""{"enabled":"yes","onTransferSent":true,"onTransferReceived":true,"onDeposit":true}""")]
    [InlineData("""{"enabled":true,"onTransferSent":true,"onTransferReceived":true,"onDeposit":true,"extra":1}""")]
    [InlineData("""{"enabled":true,"onTransferSent":true,"onTransferReceived":true,"onDeposit":true,"userId":"00000000-0000-0000-0000-000000000001"}""")]
    [InlineData("не json")]
    public async Task PutSettings_InvalidBody_Returns400AndDoesNotCallGateway(string body)
    {
        var (factory, gateway) = await CreateAsync();
        await using var owner = factory;
        using var client = await TestUsers.NewSignedInClientAsync(factory);

        var response = await client.PutAsync("/me/notifications/settings", Json(body));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(gateway.Calls);
    }

    [Fact]
    public async Task GatewayUnavailable_Returns503WithCode_AndRestOfApiKeepsWorking()
    {
        var (factory, gateway) = await CreateAsync();
        await using var owner = factory;
        using var client = await TestUsers.NewSignedInClientAsync(factory);
        gateway.Failure = new NotificationsUnavailableException();

        var response = await client.GetAsync("/me/notifications");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("notifications_unavailable", problem.GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/accounts")).StatusCode);
    }

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task WithoutBaseUrl_AllRoutesReturn503(string method, string path)
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        using var client = await TestUsers.NewSignedInClientAsync(factory);

        var response = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), path)
        {
            Content = Json("""{"enabled":true,"onTransferSent":true,"onTransferReceived":true,"onDeposit":true}"""),
        });

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("notifications_unavailable", problem.GetProperty("code").GetString());
    }

    [Fact]
    public async Task BaseUrlWithoutSecret_FailsAtStartup()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres, settings: new Dictionary<string, string?>
        {
            ["Notifications:BaseUrl"] = "http://notifications:8080",
        });

        Assert.ThrowsAny<Exception>(() => factory.CreateClient());
    }

    // --- NotificationsHttpGateway ---

    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            return respond(request);
        }
    }

    private static (NotificationsHttpGateway Gateway, StubHandler Handler) CreateGateway(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> respond)
    {
        var handler = new StubHandler(respond);
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://notifications:8080") };
        return (new NotificationsHttpGateway(http, new NotificationsGatewayOptions { BaseUrl = "http://notifications:8080", InternalSecret = Secret }), handler);
    }

    private static HttpResponseMessage JsonResponse(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task Gateway_SendsSecretAndUserPath_AndParsesStatus()
    {
        var user = Guid.NewGuid();
        var (gateway, handler) = CreateGateway(_ => Task.FromResult(JsonResponse(HttpStatusCode.OK,
            """{"linked":true,"linkedAt":"2026-11-02T09:00:00+00:00","settings":{"enabled":true,"onTransferSent":false,"onTransferReceived":true,"onDeposit":true}}""")));

        var status = await gateway.GetAsync(user, default);

        var request = Assert.Single(handler.Requests);
        Assert.Equal($"/internal/users/{user}/telegram", request.RequestUri!.AbsolutePath);
        Assert.Equal(Secret, Assert.Single(request.Headers.GetValues("X-Internal-Secret")));
        Assert.True(status.Linked);
        Assert.False(status.Settings.OnTransferSent);
    }

    [Fact]
    public async Task Gateway_CreateLinkUnlinkAndSettings_UseInternalRoutes()
    {
        var user = Guid.NewGuid();
        var (gateway, handler) = CreateGateway(request => Task.FromResult(request.Method == HttpMethod.Post
            ? JsonResponse(HttpStatusCode.OK, """{"url":"https://t.me/bot?start=x","expiresAt":"2026-11-02T09:10:00+00:00"}""")
            : new HttpResponseMessage(HttpStatusCode.NoContent)));

        var link = await gateway.CreateLinkAsync(user, default);
        await gateway.UnlinkAsync(user, default);
        await gateway.UpdateSettingsAsync(user, new NotificationSettingsDto(true, true, false, true), default);

        Assert.Equal("https://t.me/bot?start=x", link.Url);
        Assert.Equal(
            new[]
            {
                ("POST", $"/internal/users/{user}/telegram/link-code"),
                ("DELETE", $"/internal/users/{user}/telegram"),
                ("PUT", $"/internal/users/{user}/telegram/settings"),
            },
            handler.Requests.Select(r => (r.Method.Method, r.RequestUri!.AbsolutePath)).ToArray());
    }

    [Fact]
    public async Task Gateway_NetworkError_ThrowsUnavailableWithoutSecret()
    {
        var (gateway, _) = CreateGateway(_ => throw new HttpRequestException($"refused, header {Secret}"));

        var ex = await Assert.ThrowsAsync<NotificationsUnavailableException>(() => gateway.GetAsync(Guid.NewGuid(), default));

        Assert.DoesNotContain(Secret, ex.ToString());
    }

    [Fact]
    public async Task Gateway_Timeout_ThrowsUnavailable()
    {
        var (gateway, _) = CreateGateway(_ => throw new TaskCanceledException("timeout"));
        await Assert.ThrowsAsync<NotificationsUnavailableException>(() => gateway.GetAsync(Guid.NewGuid(), default));
    }

    [Fact]
    public async Task Gateway_CallerCancellation_IsNotReportedAsUnavailable()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var (gateway, _) = CreateGateway(_ => throw new TaskCanceledException("cancelled", null, cts.Token));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => gateway.GetAsync(Guid.NewGuid(), cts.Token));
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.Unauthorized)]
    public async Task Gateway_ErrorStatus_ThrowsUnavailableWithoutSecret(HttpStatusCode status)
    {
        var (gateway, _) = CreateGateway(_ => Task.FromResult(JsonResponse(status, $$"""{"detail":"{{Secret}}"}""")));

        var ex = await Assert.ThrowsAsync<NotificationsUnavailableException>(() => gateway.UnlinkAsync(Guid.NewGuid(), default));

        Assert.DoesNotContain(Secret, ex.ToString());
    }

    [Fact]
    public async Task Gateway_MalformedBody_ThrowsUnavailable()
    {
        var (gateway, _) = CreateGateway(_ => Task.FromResult(JsonResponse(HttpStatusCode.OK, "not json")));
        await Assert.ThrowsAsync<NotificationsUnavailableException>(() => gateway.GetAsync(Guid.NewGuid(), default));
    }
}
