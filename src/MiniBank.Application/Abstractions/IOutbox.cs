using MiniBank.Contracts;

namespace MiniBank.Application.Abstractions;

/// <summary>Складывает событие в outbox; попадает в БД вместе с <see cref="IUnitOfWork.SaveChangesAsync"/> операции.</summary>
public interface IOutbox
{
    void Add(IntegrationEvent @event, DateTimeOffset occurredAt);
}
