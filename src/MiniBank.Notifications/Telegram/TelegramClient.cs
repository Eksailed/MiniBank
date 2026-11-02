using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace MiniBank.Notifications.Telegram;

/// <summary>
/// Клиент Bot API. Токен стоит в пути запроса, поэтому адрес нигде не попадает в сообщения исключений и логи:
/// ошибки описываются только методом и кодом ответа.
/// </summary>
public sealed class TelegramClient(HttpClient http, TelegramOptions options) : ITelegramClient
{
    private static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan DefaultRetryAfter = TimeSpan.FromSeconds(5);

    public async Task SendMessageAsync(long chatId, string text, CancellationToken ct)
    {
        using var _ = await CallAsync("sendMessage", new Dictionary<string, object> { ["chat_id"] = chatId, ["text"] = text }, SendTimeout, ct);
    }

    public async Task<IReadOnlyList<TelegramUpdate>> GetUpdatesAsync(long offset, TimeSpan timeout, CancellationToken ct)
    {
        using var result = await CallAsync(
            "getUpdates",
            new Dictionary<string, object> { ["offset"] = offset, ["timeout"] = (int)timeout.TotalSeconds },
            timeout + SendTimeout, ct);

        var updates = new List<TelegramUpdate>();
        if (!result.RootElement.TryGetProperty("result", out var items) || items.ValueKind != JsonValueKind.Array)
            return updates;

        foreach (var item in items.EnumerateArray())
        {
            long? chatId = null;
            string? text = null;
            if (item.TryGetProperty("message", out var message))
            {
                if (message.TryGetProperty("chat", out var chat) && chat.TryGetProperty("id", out var id))
                    chatId = id.GetInt64();
                if (message.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String)
                    text = t.GetString();
            }

            updates.Add(new TelegramUpdate(item.GetProperty("update_id").GetInt64(), chatId, text));
        }

        return updates;
    }

    private async Task<JsonDocument> CallAsync(string method, object body, TimeSpan timeout, CancellationToken ct)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutSource.CancelAfter(timeout);
        try
        {
            using var response = await http.PostAsJsonAsync($"{options.ApiBaseUrl}/bot{options.BotToken}/{method}", body, timeoutSource.Token);
            var content = await response.Content.ReadAsStringAsync(timeoutSource.Token);
            var status = (int)response.StatusCode;

            if (response.IsSuccessStatusCode)
                return JsonDocument.Parse(content);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
                throw new TelegramRateLimitedException(ParseRetryAfter(content));
            if (response.StatusCode == HttpStatusCode.Forbidden)
                throw new TelegramForbiddenException();
            if (status >= 500)
                throw new TelegramUnavailableException($"{method}: HTTP {status}");
            throw new TelegramRejectedException($"{method}: HTTP {status}");
        }
        catch (HttpRequestException)
        {
            throw new TelegramUnavailableException($"{method}: network error");
        }
        catch (JsonException)
        {
            throw new TelegramUnavailableException($"{method}: malformed response");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TelegramUnavailableException($"{method}: timeout");
        }
    }

    private static TimeSpan ParseRetryAfter(string content)
    {
        try
        {
            using var doc = JsonDocument.Parse(content);
            if (doc.RootElement.TryGetProperty("parameters", out var p) && p.TryGetProperty("retry_after", out var r) && r.TryGetInt32(out var seconds))
                return TimeSpan.FromSeconds(Math.Max(seconds, 1));
        }
        catch (JsonException)
        {
        }

        return DefaultRetryAfter;
    }
}
