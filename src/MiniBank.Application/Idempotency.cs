using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MiniBank.Application.Abstractions;
using MiniBank.Domain;

namespace MiniBank.Application;

public sealed record IdempotentResult<T>(T Value, bool Replayed);

/// <summary>Ключ уже использован для другого запроса (другое тело или другая операция).</summary>
public sealed class IdempotencyKeyReuseException(string message) : Exception(message);

/// <summary>
/// Выполняет операцию ровно один раз на ключ: резерв ключа, сама операция и сохранение ответа
/// идут в одной транзакции, поэтому неудавшийся запрос ключ не занимает.
/// </summary>
public sealed class IdempotencyExecutor(IUnitOfWork uow, IIdempotencyStore store)
{
    public const int MaxKeyLength = 200;

    private const int SuccessStatusCode = 200;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<IdempotentResult<T>> ExecuteAsync<T>(
        Guid userId, string? key, string requestHash, Func<CancellationToken, Task<T>> operation, CancellationToken ct)
    {
        var validKey = ScopeToUser(userId, ValidateKey(key));

        return await uow.ExecuteInTransactionAsync(async token =>
        {
            var stored = await store.TryReserveAsync(validKey, requestHash, token);
            if (stored is not null)
            {
                if (stored.RequestHash != requestHash)
                    throw new IdempotencyKeyReuseException(
                        "Idempotency-Key was already used with a different request.");

                var replayed = JsonSerializer.Deserialize<T>(stored.Body, Json)
                    ?? throw new InvalidOperationException("Stored idempotent response is empty.");
                return new IdempotentResult<T>(replayed, Replayed: true);
            }

            var value = await operation(token);
            await store.CompleteAsync(validKey, SuccessStatusCode, JsonSerializer.Serialize(value, Json), token);
            return new IdempotentResult<T>(value, Replayed: false);
        }, ct);
    }

    /// <summary>
    /// Ключ хранится вместе с владельцем: чужой запрос с тем же ключом не получает ни чужой ответ, ни 422.
    /// Хэш исходного ключа держит длину фиксированной (≤ 200 символов колонки).
    /// </summary>
    private static string ScopeToUser(Guid userId, string key) =>
        $"{userId:N}:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))}";

    private static string ValidateKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new DomainValidationException("Idempotency-Key header is required.");
        if (key.Length > MaxKeyLength)
            throw new DomainValidationException($"Idempotency-Key must be at most {MaxKeyLength} characters.");
        if (key.Any(char.IsControl))
            throw new DomainValidationException("Idempotency-Key must not contain control characters.");
        return key;
    }
}
