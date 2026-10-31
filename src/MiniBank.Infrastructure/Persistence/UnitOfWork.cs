using Microsoft.EntityFrameworkCore;
using MiniBank.Application;
using MiniBank.Application.Abstractions;
using Npgsql;

namespace MiniBank.Infrastructure.Persistence;

internal sealed class UnitOfWork(MiniBankDbContext db) : IUnitOfWork
{
    private const int MaxAttempts = 3;

    public async Task<T> ExecuteInTransactionAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is not null)
            return await operation(ct);

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await using var transaction = await db.Database.BeginTransactionAsync(ct);
                var result = await operation(ct);
                await transaction.CommitAsync(ct);
                return result;
            }
            catch (Exception ex) when (IsDeadlockOrSerializationFailure(ex))
            {
                db.ChangeTracker.Clear();
                if (attempt >= MaxAttempts)
                    throw new ConcurrencyConflictException("The operation conflicted with concurrent requests. Retry the request.");
            }
        }
    }

    private static bool IsDeadlockOrSerializationFailure(Exception ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (current is PostgresException { SqlState: PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.DeadlockDetected })
                return true;
        }
        return false;
    }

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
