namespace MiniBank.Infrastructure.Persistence;

/// <summary>Строка таблицы idempotency_keys. Доступ — только через <see cref="IdempotencyStore"/>.</summary>
public sealed class IdempotencyKeyRecord
{
    public string Key { get; set; } = "";
    public string RequestHash { get; set; } = "";
    public int StatusCode { get; set; }
    public string ResponseBody { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
}
