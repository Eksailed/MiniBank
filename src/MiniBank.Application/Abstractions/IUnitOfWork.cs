namespace MiniBank.Application.Abstractions;

public interface IUnitOfWork
{
    /// <exception cref="ConcurrencyConflictException">Счёт изменён другой операцией после чтения.</exception>
    Task SaveChangesAsync(CancellationToken ct);

    /// <summary>
    /// Выполняет операцию в одной транзакции. Если БД сообщает о взаимной блокировке или ошибке
    /// сериализации (SQLSTATE 40P01, 40001), операция повторяется целиком, всего до 3 попыток.
    /// Если транзакция уже открыта, операция просто выполняется в ней.
    /// </summary>
    /// <exception cref="ConcurrencyConflictException">Попытки исчерпаны.</exception>
    Task<T> ExecuteInTransactionAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken ct);
}
