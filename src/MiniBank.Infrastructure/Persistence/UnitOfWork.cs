using Microsoft.EntityFrameworkCore;
using MiniBank.Application;
using MiniBank.Application.Abstractions;

namespace MiniBank.Infrastructure.Persistence;

internal sealed class UnitOfWork(MiniBankDbContext db) : IUnitOfWork
{
    public async Task SaveChangesAsync(CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new ConcurrencyConflictException("The account was modified by another operation. Retry the request.");
        }
    }
}
