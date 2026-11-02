using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MiniBank.Contracts;
using Npgsql;

namespace MiniBank.Notifications;

public enum HandleResult { Processed, Duplicate, Ignored }

/// <summary>Тело сообщения не разобрать: повтор не поможет, сообщение сразу уходит в dead-letter.</summary>
public sealed class MalformedEventException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Разбирает конверт и записывает событие. Отметка <c>processed_events</c> и записи <c>received_events</c> идут
/// одной транзакцией, поэтому повторная доставка того же <c>eventId</c> ничего не меняет.
/// </summary>
public sealed class EventHandler(IDbContextFactory<NotificationsDbContext> dbFactory, ILogger<EventHandler> logger)
{
    private const int SupportedVersion = 1;
    private const string UniqueViolation = "23505";

    public async Task<HandleResult> HandleAsync(ReadOnlyMemory<byte> body, CancellationToken ct)
    {
        var envelope = ParseEnvelope(body);
        if (envelope.Version != SupportedVersion || envelope.Type is not (EventTypes.TransferCompleted or EventTypes.AccountDeposited))
        {
            logger.LogWarning("Ignoring event {EventId} of unsupported type {Type} version {Version}",
                envelope.EventId, envelope.Type, envelope.Version);
            return HandleResult.Ignored;
        }

        var owners = ParseOwners(envelope);

        await using var db = dbFactory.CreateDbContext();
        if (await db.ProcessedEvents.AnyAsync(e => e.EventId == envelope.EventId, ct))
            return HandleResult.Duplicate;

        var now = DateTimeOffset.UtcNow;
        db.ProcessedEvents.Add(new ProcessedEvent { EventId = envelope.EventId, ProcessedAt = now });
        foreach (var owner in owners)
        {
            db.ReceivedEvents.Add(new ReceivedEvent
            {
                Id = Guid.NewGuid(),
                EventId = envelope.EventId,
                Type = envelope.Type,
                OwnerId = owner,
                OccurredAt = envelope.OccurredAt,
                ReceivedAt = now,
            });
        }

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: UniqueViolation })
        {
            // Параллельная доставка того же события успела раньше.
            return HandleResult.Duplicate;
        }

        logger.LogInformation("Processed event {EventId} ({Type}) for {Owners} owner(s)", envelope.EventId, envelope.Type, owners.Count);
        return HandleResult.Processed;
    }

    private static EventEnvelope ParseEnvelope(ReadOnlyMemory<byte> body)
    {
        EventEnvelope? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<EventEnvelope>(body.Span, EventJson.Options);
        }
        catch (JsonException ex)
        {
            throw new MalformedEventException("The message body is not a valid event envelope.", ex);
        }

        if (envelope is null || envelope.EventId == Guid.Empty || string.IsNullOrEmpty(envelope.Type))
            throw new MalformedEventException("The event envelope has no eventId or type.");
        return envelope;
    }

    private static IReadOnlyList<Guid> ParseOwners(EventEnvelope envelope)
    {
        try
        {
            if (envelope.Type == EventTypes.TransferCompleted)
            {
                var transfer = envelope.Data.Deserialize<TransferCompleted>(EventJson.Options)
                    ?? throw new MalformedEventException("The event data is empty.");
                // Перевод между своими счетами даёт одного владельца: уведомление одно.
                return transfer.ToOwnerId is { } to && to != transfer.FromOwnerId
                    ? [transfer.FromOwnerId, to]
                    : [transfer.FromOwnerId];
            }

            var deposit = envelope.Data.Deserialize<AccountDeposited>(EventJson.Options)
                ?? throw new MalformedEventException("The event data is empty.");
            return [deposit.OwnerId];
        }
        catch (JsonException ex)
        {
            throw new MalformedEventException("The event data does not match its type.", ex);
        }
        catch (InvalidOperationException ex)
        {
            // Нет data: JsonElement по умолчанию не десериализуется.
            throw new MalformedEventException("The event has no data.", ex);
        }
    }
}
