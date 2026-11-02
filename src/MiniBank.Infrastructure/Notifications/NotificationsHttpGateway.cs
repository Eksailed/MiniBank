using System.Net.Http.Json;
using System.Text.Json;
using MiniBank.Application;
using MiniBank.Application.Abstractions;

namespace MiniBank.Infrastructure.Notifications;

/// <summary>Вызывает внутренний API сервиса уведомлений. Тексты ошибок не содержат секрета и тел ответов.</summary>
public sealed class NotificationsHttpGateway(HttpClient http, NotificationsGatewayOptions options) : INotificationsGateway
{
    private const string SecretHeader = "X-Internal-Secret";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public Task<TelegramStatusDto> GetAsync(Guid userId, CancellationToken ct) =>
        SendAsync<TelegramStatusDto>(HttpMethod.Get, $"/internal/users/{userId}/telegram", null, ct);

    public Task<LinkDto> CreateLinkAsync(Guid userId, CancellationToken ct) =>
        SendAsync<LinkDto>(HttpMethod.Post, $"/internal/users/{userId}/telegram/link-code", null, ct);

    public Task UnlinkAsync(Guid userId, CancellationToken ct) =>
        SendAsync<object>(HttpMethod.Delete, $"/internal/users/{userId}/telegram", null, ct);

    public Task UpdateSettingsAsync(Guid userId, NotificationSettingsDto settings, CancellationToken ct) =>
        SendAsync<object>(HttpMethod.Put, $"/internal/users/{userId}/telegram/settings", settings, ct);

    private async Task<T> SendAsync<T>(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add(SecretHeader, options.InternalSecret);
        if (body is not null)
            request.Content = JsonContent.Create(body, options: Json);

        try
        {
            using var response = await http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
                throw new NotificationsUnavailableException();
            if (typeof(T) == typeof(object))
                return default!;
            return await response.Content.ReadFromJsonAsync<T>(Json, ct) ?? throw new NotificationsUnavailableException();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException or NotSupportedException)
        {
            throw new NotificationsUnavailableException();
        }
    }
}
