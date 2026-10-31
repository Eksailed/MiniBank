namespace MiniBank.Application.Abstractions;

public sealed record StoredResponse(string RequestHash, int StatusCode, string Body);

/// <summary>Ключи идемпотентности. Методы вызываются внутри открытой транзакции.</summary>
public interface IIdempotencyStore
{
    /// <summary>
    /// Резервирует ключ. Возвращает <c>null</c>, если ключ был свободен и теперь занят этой транзакцией;
    /// иначе возвращает сохранённую запись. Если ключ занят незавершённой транзакцией, ждёт её окончания.
    /// </summary>
    Task<StoredResponse?> TryReserveAsync(string key, string requestHash, CancellationToken ct);

    /// <summary>Сохраняет ответ в ранее зарезервированной строке.</summary>
    Task CompleteAsync(string key, int statusCode, string body, CancellationToken ct);
}
