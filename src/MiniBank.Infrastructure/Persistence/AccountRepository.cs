using MiniBank.Application.Abstractions;
using MiniBank.Domain;

namespace MiniBank.Infrastructure.Persistence;

internal sealed class AccountRepository(MiniBankDbContext db) : IAccountRepository
{
    public async Task<Account?> FindAsync(Guid id, CancellationToken ct) =>
        await db.Accounts.FindAsync([id], ct);

    public void Add(Account account) => db.Accounts.Add(account);
}
