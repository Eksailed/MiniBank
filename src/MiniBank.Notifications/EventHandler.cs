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
        var ownerIds = owners.Select(o => o.OwnerId).ToList();
        var links = await db.TelegramLinks.Where(l => ownerIds.Contains(l.UserId)).ToDictionaryAsync(l => l.UserId, ct);
        var settings = await db.NotificationSettings.Where(s => ownerIds.Contains(s.UserId)).ToDictionaryAsync(s => s.UserId, ct);
        foreach (var owner in owners)
        {
            var received = new ReceivedEvent
            {
                Id = Guid.NewGuid(),
                EventId = envelope.EventId,
                Type = envelope.Type,
                OwnerId = owner.OwnerId,
                OccurredAt = envelope.OccurredAt,
                ReceivedAt = now,
            };
            db.ReceivedEvents.Add(received);

            // Доставка ставится в очередь в той же транзакции: событие не теряется и не дублируется.
            if (links.TryGetValue(owner.OwnerId, out var link) && IsAllowed(settings.GetValueOrDefault(owner.OwnerId), owner.Kind))
            {
                db.TelegramDeliveries.Add(new TelegramDelivery
                {
                    Id = Guid.NewGuid(),
                    ReceivedEventId = received.Id,
                    ChatId = link.ChatId,
                    Text = owner.Text,
                    NextAttemptAt = now,
                });
            }
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

    private static bool IsAllowed(NotificationSettings? settings, NoticeKind kind) =>
        settings is null || (settings.Enabled && kind switch
        {
            NoticeKind.TransferSent => settings.OnTransferSent,
            NoticeKind.TransferReceived => settings.OnTransferReceived,
            _ => settings.OnDeposit,
        });

    private enum NoticeKind { TransferSent, TransferReceived, Deposit }

    private sealed record OwnerNotice(Guid OwnerId, NoticeKind Kind, string Text);

    private static IReadOnlyList<OwnerNotice> ParseOwners(EventEnvelope envelope)
    {
        try
        {
            if (envelope.Type == EventTypes.TransferCompleted)
            {
                var transfer = envelope.Data.Deserialize<TransferCompleted>(EventJson.Options)
                    ?? throw new MalformedEventException("The event data is empty.");
                // Перевод между своими счетами даёт одного владельца: уведомление одно.
                var sent = new OwnerNotice(transfer.FromOwnerId, NoticeKind.TransferSent,
                    MessageText.TransferSent(transfer.Amount, transfer.Currency));
                return transfer.ToOwnerId is { } to && to != transfer.FromOwnerId
                    ? [sent, new OwnerNotice(to, NoticeKind.TransferReceived, MessageText.TransferReceived(transfer.Amount, transfer.Currency))]
                    : [sent];
            }

            var deposit = envelope.Data.Deserialize<AccountDeposited>(EventJson.Options)
                ?? throw new MalformedEventException("The event data is empty.");
            return [new OwnerNotice(deposit.OwnerId, NoticeKind.Deposit, MessageText.Deposit(deposit.Amount, deposit.Currency))];
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
