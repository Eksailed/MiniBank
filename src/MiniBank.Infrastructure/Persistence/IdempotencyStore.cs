using Microsoft.EntityFrameworkCore;
using MiniBank.Application.Abstractions;

namespace MiniBank.Infrastructure.Persistence;

internal sealed class IdempotencyStore(MiniBankDbContext db, TimeProvider clock) : IIdempotencyStore
{
    public async Task<StoredResponse?> TryReserveAsync(string key, string requestHash, CancellationToken ct)
    {
        // Заглушка (status_code = 0) видна только этой транзакции: параллельный INSERT с тем же ключом
        // ждёт на уникальном индексе, пока она не зафиксируется или не откатится.
        var inserted = await db.Database.ExecuteSqlAsync($"""
            INSERT INTO idempotency_keys (key, request_hash, status_code, response_body, created_at)
            VALUES ({key}, {requestHash}, 0, '', {clock.GetUtcNow()})
            ON CONFLICT (key) DO NOTHING
            """, ct);
        if (inserted == 1)
            return null;

        var existing = await db.Set<IdempotencyKeyRecord>().AsNoTracking().SingleAsync(r => r.Key == key, ct);
        return new StoredResponse(existing.RequestHash, existing.StatusCode, existing.ResponseBody);
    }

    public async Task CompleteAsync(string key, int statusCode, string body, CancellationToken ct) =>
        await db.Database.ExecuteSqlAsync($"""
            UPDATE idempotency_keys SET status_code = {statusCode}, response_body = {body} WHERE key = {key}
            """, ct);
}
