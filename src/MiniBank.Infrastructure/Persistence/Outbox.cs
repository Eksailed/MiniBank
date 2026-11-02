using System.Text.Json;
using MiniBank.Application.Abstractions;
using MiniBank.Contracts;

namespace MiniBank.Infrastructure.Persistence;

internal sealed class Outbox(MiniBankDbContext db) : IOutbox
{
    public void Add(IntegrationEvent @event, DateTimeOffset occurredAt) =>
        db.OutboxMessages.Add(new OutboxMessage
        {
            Id = Guid.NewGuid(),
            Type = @event.Type,
            Version = @event.Version,
            Payload = JsonSerializer.Serialize(@event, @event.GetType(), EventJson.Options),
            CreatedAt = occurredAt,
            OccurredAt = occurredAt,
            NextAttemptAt = occurredAt,
        });
}
