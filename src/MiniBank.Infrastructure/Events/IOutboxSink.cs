using MiniBank.Infrastructure.Persistence;

namespace MiniBank.Infrastructure.Events;

/// <summary>Куда уходит строка outbox. Возврат без исключения означает «брокер подтвердил».</summary>
public interface IOutboxSink
{
    Task PublishAsync(OutboxMessage message, CancellationToken ct);
}

/// <summary>
/// Отказ самого брокера (нет соединения, канал закрыт, не пришло подтверждение), а не конкретного сообщения.
/// Пачка прерывается без траты попыток: иначе долгий простой брокера перевёл бы все строки в «мёртвые».
/// </summary>
public sealed class BrokerUnavailableException(string message, Exception inner) : Exception(message, inner);
