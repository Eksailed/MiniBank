using Microsoft.EntityFrameworkCore;
using MiniBank.Application.Abstractions;
using MiniBank.Application.Statements;
using MiniBank.Domain;

namespace MiniBank.Infrastructure.Persistence;

internal sealed class LedgerQuery(MiniBankDbContext db) : ILedgerQuery
{
    public async Task<IReadOnlyList<LedgerEntry>> GetEntriesAsync(
        Guid accountId, EntryFilter filter, int page, int pageSize, CancellationToken ct) =>
        await Ordered(accountId, filter)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

    public async Task<int> CountAsync(Guid accountId, EntryFilter filter, int cap, CancellationToken ct) =>
        await Matching(accountId, filter).Take(cap + 1).CountAsync(ct);

    public IAsyncEnumerable<LedgerEntry> StreamAsync(Guid accountId, EntryFilter filter, CancellationToken ct) =>
        Ordered(accountId, filter).AsAsyncEnumerable();

    private IQueryable<LedgerEntry> Ordered(Guid accountId, EntryFilter filter) =>
        Matching(accountId, filter)
            .OrderByDescending(e => e.CreatedAt)
            .ThenBy(e => e.Id);

    private IQueryable<LedgerEntry> Matching(Guid accountId, EntryFilter filter)
    {
        var query = db.LedgerEntries.AsNoTracking().Where(e => e.AccountId == accountId);
        if (filter.From is { } from)
            query = query.Where(e => e.CreatedAt >= from);
        if (filter.To is { } to)
            query = query.Where(e => e.CreatedAt < to);
        return filter.Direction switch
        {
            EntryDirection.In => query.Where(e => e.Amount > 0),
            EntryDirection.Out => query.Where(e => e.Amount < 0),
            _ => query
        };
    }

    public async Task<long> GetSpentSinceAsync(Guid accountId, DateTimeOffset since, CancellationToken ct) =>
        await db.LedgerEntries
            .AsNoTracking()
            .Where(e => e.AccountId == accountId && e.Amount < 0 && e.CreatedAt >= since)
            .SumAsync(e => -e.Amount, ct);
}
