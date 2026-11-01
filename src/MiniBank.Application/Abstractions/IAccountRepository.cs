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

    /// <summary>Счета пользователя: сначала старые (created_at, затем id).</summary>
    Task<IReadOnlyList<Account>> ListByUserAsync(Guid userId, CancellationToken ct);

    Task<int> CountByUserAsync(Guid userId, CancellationToken ct);

    /// <summary>Принадлежит ли счёт пользователю. Не загружает счёт в контекст, поэтому безопасно вызывать до блокирующего чтения.</summary>
    Task<bool> IsOwnedByAsync(Guid accountId, Guid userId, CancellationToken ct);

    void Add(Account account);
}
