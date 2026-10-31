using Microsoft.EntityFrameworkCore;
using MiniBank.Application.Abstractions;
using MiniBank.Domain;

namespace MiniBank.Infrastructure.Persistence;

internal sealed class AccountRepository(MiniBankDbContext db) : IAccountRepository
{
    public async Task<Account?> FindAsync(Guid id, CancellationToken ct) =>
        await db.Accounts.FindAsync([id], ct);

    public async Task<IReadOnlyDictionary<Guid, Account>> FindForUpdateAsync(
        IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        var accounts = await db.Accounts
            .FromSqlRaw("SELECT * FROM accounts WHERE id = ANY({0}) ORDER BY id FOR UPDATE", ids.Distinct().ToArray())
            .ToListAsync(ct);
        return accounts.ToDictionary(a => a.Id);
    }

    public void Add(Account account) => db.Accounts.Add(account);
}
