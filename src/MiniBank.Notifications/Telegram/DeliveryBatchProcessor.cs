using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MiniBank.Notifications.Telegram;

/// <summary>
/// Одна пачка доставок: строки блокируются <c>FOR UPDATE SKIP LOCKED</c>, отправляются, результат фиксируется одной
/// транзакцией. Недоступность Telegram попыток не тратит: пачка прерывается, строки ждут.
/// </summary>
public sealed class DeliveryBatchProcessor(
    IDbContextFactory<NotificationsDbContext> dbFactory, TelegramOptions options, TimeProvider clock,
    ITelegramClient telegram, ILogger<DeliveryBatchProcessor> logger)
{
    private const int MaxErrorLength = 2000;
    private const int CleanupBatchSize = 500;
    private static readonly TimeSpan BaseDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MaxDelay = TimeSpan.FromMinutes(5);

    /// <summary>Пауза после <paramref name="attempts"/>-й неудачи: <c>5с·2^(n−1)</c>, но не больше 5 минут.</summary>
    public static TimeSpan Backoff(int attempts)
    {
        var shift = Math.Clamp(attempts - 1, 0, 20);
        return TimeSpan.FromTicks(Math.Min(BaseDelay.Ticks << shift, MaxDelay.Ticks));
    }

    /// <returns>Сколько доставок обработано (отправлено, отложено или провалено).</returns>
    public async Task<int> ProcessBatchAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        var rows = await db.TelegramDeliveries
            .FromSqlInterpolated($"""
                SELECT * FROM notifications.telegram_deliveries
                WHERE status = 'Pending' AND next_attempt_at <= {now}
                ORDER BY next_attempt_at
                LIMIT {options.DeliveryBatchSize}
                FOR UPDATE SKIP LOCKED
                """)
            .ToListAsync(ct);

        // Сообщение уходит только в чат, который и сейчас привязан к владельцу события: чат могли перепривязать
        // или отвязать, пока доставка ждала в очереди.
        var eventIds = rows.Select(r => r.ReceivedEventId).ToList();
        var owners = await db.ReceivedEvents.Where(e => eventIds.Contains(e.Id))
            .ToDictionaryAsync(e => e.Id, e => e.OwnerId, ct);
        var chatIds = rows.Select(r => r.ChatId).Distinct().ToList();
        var linkedChats = (await db.TelegramLinks.Where(l => chatIds.Contains(l.ChatId))
            .Select(l => new { l.ChatId, l.UserId }).ToListAsync(ct))
            .Select(l => (l.ChatId, l.UserId)).ToHashSet();

        var processed = 0;
        var forbiddenChats = new HashSet<long>();
        foreach (var row in rows)
        {
            if (!owners.TryGetValue(row.ReceivedEventId, out var owner) || !linkedChats.Contains((row.ChatId, owner)))
            {
                Fail(row, "chat relinked", now);
                processed++;
                continue;
            }

            if (forbiddenChats.Contains(row.ChatId))
            {
                Fail(row, "Forbidden", now);
                processed++;
                continue;
            }

            try
            {
                await telegram.SendMessageAsync(row.ChatId, row.Text, ct);
                row.Status = DeliveryStatus.Sent;
                row.SentAt = now;
                row.LastError = null;
                processed++;
            }
            catch (TelegramUnavailableException ex)
            {
                row.LastError = Describe(ex);
                logger.LogWarning("Telegram is unavailable; batch aborted, {Left} deliveries left for next pass", rows.Count - processed);
                break;
            }
            catch (TelegramForbiddenException)
            {
                forbiddenChats.Add(row.ChatId);
                Fail(row, "Forbidden", now);
                processed++;
            }
            catch (TelegramRateLimitedException ex)
            {
                RegisterFailure(row, ex, now, ex.RetryAfter);
                processed++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                RegisterFailure(row, ex, now, TimeSpan.Zero);
                processed++;
            }
        }

        // 403: бот заблокирован — снимаем привязку и закрываем остальные ожидающие сообщения этого чата.
        await db.SaveChangesAsync(ct);
        foreach (var chatId in forbiddenChats)
        {
            await db.TelegramLinks.Where(l => l.ChatId == chatId).ExecuteDeleteAsync(ct);
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE notifications.telegram_deliveries
                SET status = 'Failed', last_error = 'Forbidden', next_attempt_at = {now}
                WHERE chat_id = {chatId} AND status = 'Pending'
                """, ct);
            logger.LogWarning("Telegram blocked the bot in a chat; link removed");
        }

        await transaction.CommitAsync(ct);
        return processed;
    }

    /// <summary>Удаляет отправленные и проваленные доставки старше срока хранения (небольшой пачкой).</summary>
    public async Task<int> CleanupAsync(CancellationToken ct)
    {
        var cutoff = clock.GetUtcNow().AddDays(-options.RetentionDays);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Database.ExecuteSqlInterpolatedAsync($"""
            DELETE FROM notifications.telegram_deliveries WHERE id IN (
                SELECT id FROM notifications.telegram_deliveries
                WHERE (status = 'Sent' AND sent_at < {cutoff}) OR (status = 'Failed' AND next_attempt_at < {cutoff})
                LIMIT {CleanupBatchSize})
            """, ct);
    }

    private static void Fail(TelegramDelivery row, string error, DateTimeOffset now)
    {
        row.Status = DeliveryStatus.Failed;
        row.LastError = error;
        row.NextAttemptAt = now;
    }

    private void RegisterFailure(TelegramDelivery row, Exception ex, DateTimeOffset now, TimeSpan minimumDelay)
    {
        row.Attempts++;
        row.LastError = Describe(ex);

        if (row.Attempts >= options.DeliveryMaxAttempts)
        {
            row.Status = DeliveryStatus.Failed;
            row.NextAttemptAt = now;
            logger.LogError("Delivery {DeliveryId} failed after {Attempts} attempts", row.Id, row.Attempts);
            return;
        }

        var delay = Backoff(row.Attempts);
        row.NextAttemptAt = now + (delay > minimumDelay ? delay : minimumDelay);
        logger.LogWarning("Delivery {DeliveryId} failed on attempt {Attempt}: {ErrorType}", row.Id, row.Attempts, ex.GetType().Name);
    }

    private string Describe(Exception ex)
    {
        var error = $"{ex.GetType().Name}: {ex.Message}".Replace(options.BotToken, "***");
        return error.Length > MaxErrorLength ? error[..MaxErrorLength] : error;
    }
}
