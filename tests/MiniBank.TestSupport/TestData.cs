using MiniBank.Domain;
using MiniBank.Infrastructure.Persistence;

namespace MiniBank.TestSupport;

/// <summary>Единая точка создания пользователей и счетов в тестах.</summary>
public static class TestData
{
    private static readonly DateTimeOffset Now = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Сохраняет пользователя с подтверждённым email и произвольным (не пригодным для входа) хэшем пароля.</summary>
    public static async Task<User> SeedUserAsync(MiniBankDbContext db, string? email = null)
    {
        var user = User.Register(email ?? $"user-{Guid.NewGuid():N}@example.com", "test-hash", "Alice", Now);
        user.ConfirmEmail(Now);
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    /// <summary>Лимиты нового счёта в тестах: совпадают с умолчаниями рублёвого счёта из <c>appsettings.json</c>.</summary>
    public static AccountLimits DefaultLimits { get; } = new(10_000_000, 30_000_000);

    /// <summary>Несистемный счёт пользователя; в БД попадает только после <c>Add</c> и сохранения.</summary>
    public static Account OpenFor(Guid userId, string currency = "RUB", string owner = "Alice") =>
        Account.Open(owner, currency, Now, userId: userId, limits: DefaultLimits);
}
