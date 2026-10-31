using MiniBank.Domain;

namespace MiniBank.Application.Abstractions;

public interface IAccountRepository
{
    Task<Account?> FindAsync(Guid id, CancellationToken ct);

    /// <summary>
    /// Блокирует и загружает существующие счета одним запросом в порядке id (SELECT ... FOR UPDATE).
    /// Отсутствующие id в результат не попадают. Вызывать внутри транзакции.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, Account>> FindForUpdateAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct);

    void Add(Account account);
}
