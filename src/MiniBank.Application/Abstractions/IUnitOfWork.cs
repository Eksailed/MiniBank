namespace MiniBank.Application.Abstractions;

public interface IUnitOfWork
{
    /// <exception cref="ConcurrencyConflictException">Счёт изменён другой операцией после чтения.</exception>
    Task SaveChangesAsync(CancellationToken ct);
}
