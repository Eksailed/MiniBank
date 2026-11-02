using MiniBank.Infrastructure.Persistence;

namespace MiniBank.Infrastructure.Events;

/// <summary>Куда уходит строка outbox. Возврат без исключения означает «брокер подтвердил».</summary>
public interface IOutboxSink
{
    Task PublishAsync(OutboxMessage message, CancellationToken ct);
}
