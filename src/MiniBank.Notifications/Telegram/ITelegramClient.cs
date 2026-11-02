namespace MiniBank.Notifications.Telegram;

/// <param name="ChatId">Чат отправителя; <c>null</c>, если апдейт не сообщение (его всё равно нужно подтвердить смещением).</param>
public sealed record TelegramUpdate(long UpdateId, long? ChatId, string? Text);

public interface ITelegramClient
{
    Task SendMessageAsync(long chatId, string text, CancellationToken ct);

    Task<IReadOnlyList<TelegramUpdate>> GetUpdatesAsync(long offset, TimeSpan timeout, CancellationToken ct);
}

/// <summary>Сеть, тайм-аут или 5xx: Telegram недоступен целиком, попытки не тратятся.</summary>
public sealed class TelegramUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>429 Too Many Requests.</summary>
public sealed class TelegramRateLimitedException(TimeSpan retryAfter) : Exception("Telegram rate limit reached.")
{
    public TimeSpan RetryAfter { get; } = retryAfter;
}

/// <summary>403: бот заблокирован пользователем или удалён из чата.</summary>
public sealed class TelegramForbiddenException() : Exception("Telegram forbids sending to this chat.");

/// <summary>Прочие 4xx: запрос отвергнут.</summary>
public sealed class TelegramRejectedException(string message) : Exception(message);
