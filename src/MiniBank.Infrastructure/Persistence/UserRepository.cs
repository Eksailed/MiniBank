using Microsoft.EntityFrameworkCore;
using MiniBank.Application.Abstractions;
using MiniBank.Domain;

namespace MiniBank.Infrastructure.Persistence;

internal sealed class UserRepository(MiniBankDbContext db) : IUserRepository
{
    public async Task<User?> FindByIdAsync(Guid id, CancellationToken ct) =>
        await db.Users.FindAsync([id], ct);

    public Task<User?> FindByIdForUpdateAsync(Guid id, CancellationToken ct) =>
        db.Users
            .FromSqlRaw("SELECT * FROM users WHERE id = {0} FOR UPDATE", id)
            .FirstOrDefaultAsync(ct);

    public Task<User?> FindByEmailAsync(string normalizedEmail, CancellationToken ct) =>
        db.Users.FirstOrDefaultAsync(u => u.Email == normalizedEmail, ct);

    public Task<User?> FindByEmailForUpdateAsync(string normalizedEmail, CancellationToken ct) =>
        db.Users
            .FromSqlRaw("SELECT * FROM users WHERE email = {0} FOR UPDATE", normalizedEmail)
            .FirstOrDefaultAsync(ct);

    public Task<EmailConfirmationToken?> FindTokenByHashAsync(string hash, CancellationToken ct) =>
        db.EmailConfirmationTokens
            .FromSqlRaw("SELECT * FROM email_confirmation_tokens WHERE token_hash = {0} FOR UPDATE", hash)
            .FirstOrDefaultAsync(ct);

    public Task<EmailConfirmationToken?> FindLatestTokenAsync(Guid userId, CancellationToken ct) =>
        db.EmailConfirmationTokens
            .Where(t => t.UserId == userId)
            .OrderByDescending(t => t.CreatedAt)
            .FirstOrDefaultAsync(ct);

    public Task InvalidateOpenTokensAsync(Guid userId, DateTimeOffset now, CancellationToken ct) =>
        db.EmailConfirmationTokens
            .Where(t => t.UserId == userId && t.UsedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.UsedAt, now), ct);

    public void Add(User user) => db.Users.Add(user);

    public void Add(EmailConfirmationToken token) => db.EmailConfirmationTokens.Add(token);
}
