using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MiniBank.Infrastructure.Persistence;

namespace MiniBank.Infrastructure.Events;

/// <summary>
/// Одна пачка outbox: строки блокируются <c>FOR UPDATE SKIP LOCKED</c> (параллельные публикаторы не берут одно и то же),
/// публикуются, помечаются и фиксируются одной транзакцией. Падение между подтверждением брокера и коммитом
/// даёт повторную отправку: гарантия at-least-once, потребитель дедуплицирует по eventId.
/// </summary>
public sealed class OutboxBatchProcessor(
    MiniBankDbContext db, EventsOptions options, TimeProvider clock, ILogger<OutboxBatchProcessor> logger)
{
    private const int MaxErrorLength = 2000;
    private const int CleanupBatchSize = 500;
    private static readonly TimeSpan BaseDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MaxDelay = TimeSpan.FromMinutes(5);

    /// <returns>Сколько строк обработано (опубликовано или отложено после ошибки).</returns>
    public async Task<int> ProcessBatchAsync(IOutboxSink sink, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        var rows = await db.OutboxMessages
            .FromSqlInterpolated($"""
                SELECT * FROM outbox_messages
                WHERE published_at IS NULL AND dead_at IS NULL AND next_attempt_at <= {now}
                ORDER BY created_at
                LIMIT {options.BatchSize}
                FOR UPDATE SKIP LOCKED
                """)
            .ToListAsync(ct);

        var published = 0; // обработано: опубликовано или отложено после ошибки строки
        foreach (var row in rows)
        {
            try
            {
                await sink.PublishAsync(row, ct);
                row.PublishedAt = now;
                published++;
            }
            catch (BrokerUnavailableException ex)
            {
                // Брокер недоступен целиком: попытки не тратим и остальные строки не трогаем.
                var error = $"{ex.InnerException?.GetType().Name ?? ex.GetType().Name}: {ex.Message}";
                row.LastError = error.Length > MaxErrorLength ? error[..MaxErrorLength] : error;
                logger.LogWarning("Broker is unavailable ({ErrorType}); batch aborted, {Left} rows left for next pass",
                    ex.InnerException?.GetType().Name ?? ex.GetType().Name, rows.Count - published);
                break;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                RegisterFailure(row, ex, now);
                published++;
            }
        }

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return published;
    }

    /// <summary>Удаляет опубликованные строки старше срока хранения (небольшой пачкой).</summary>
    public Task<int> CleanupAsync(CancellationToken ct)
    {
        var cutoff = clock.GetUtcNow().AddDays(-options.RetentionDays);
        return db.Database.ExecuteSqlInterpolatedAsync($"""
            DELETE FROM outbox_messages WHERE id IN (
                SELECT id FROM outbox_messages WHERE published_at < {cutoff} LIMIT {CleanupBatchSize})
            """, ct);
    }

    /// <summary>Возраст самой старой неотправленной (и не «мёртвой») строки; <c>null</c>, если очередь пуста.</summary>
    public async Task<TimeSpan?> OldestPendingAgeAsync(CancellationToken ct)
    {
        var oldest = await db.OutboxMessages
            .Where(m => m.PublishedAt == null && m.DeadAt == null)
            .MinAsync(m => (DateTimeOffset?)m.CreatedAt, ct);
        return oldest is null ? null : clock.GetUtcNow() - oldest;
    }

    private void RegisterFailure(OutboxMessage row, Exception ex, DateTimeOffset now)
    {
        row.Attempts++;
        var error = $"{ex.GetType().Name}: {ex.Message}";
        row.LastError = error.Length > MaxErrorLength ? error[..MaxErrorLength] : error;

        if (row.Attempts >= options.MaxAttempts)
        {
            row.DeadAt = now;
            logger.LogError("Outbox event {EventId} ({Type}) is dead after {Attempt} attempts", row.Id, row.Type, row.Attempts);
            return;
        }

        var delay = TimeSpan.FromTicks(Math.Min(BaseDelay.Ticks << (row.Attempts - 1), MaxDelay.Ticks));
        row.NextAttemptAt = now + delay;
        logger.LogWarning("Outbox event {EventId} ({Type}) failed on attempt {Attempt}: {ErrorType}",
            row.Id, row.Type, row.Attempts, ex.GetType().Name);
    }
}
