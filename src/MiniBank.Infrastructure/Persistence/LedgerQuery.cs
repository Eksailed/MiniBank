using Microsoft.EntityFrameworkCore;
using MiniBank.Application.Abstractions;
using MiniBank.Domain;

namespace MiniBank.Infrastructure.Persistence;

internal sealed class LedgerQuery(MiniBankDbContext db) : ILedgerQuery
{
    public async Task<IReadOnlyList<LedgerEntry>> GetEntriesAsync(
        Guid accountId, int page, int pageSize, CancellationToken ct) =>
        await db.LedgerEntries
            .AsNoTracking()
            .Where(e => e.AccountId == accountId)
            .OrderByDescending(e => e.CreatedAt)
            .ThenBy(e => e.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

    public async Task<long> GetSpentSinceAsync(Guid accountId, DateTimeOffset since, CancellationToken ct) =>
        await db.LedgerEntries
            .AsNoTracking()
            .Where(e => e.AccountId == accountId && e.Amount < 0 && e.CreatedAt >= since)
            .SumAsync(e => -e.Amount, ct);
}
