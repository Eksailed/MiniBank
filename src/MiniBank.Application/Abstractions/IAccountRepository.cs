using MiniBank.Domain;

namespace MiniBank.Application.Abstractions;

public interface IAccountRepository
{
    Task<Account?> FindAsync(Guid id, CancellationToken ct);

    void Add(Account account);
}
