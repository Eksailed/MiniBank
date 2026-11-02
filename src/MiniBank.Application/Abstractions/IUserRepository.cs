using MiniBank.Domain;

namespace MiniBank.Application.Abstractions;

public interface IUserRepository
{
    Task<User?> FindByIdAsync(Guid id, CancellationToken ct);

    /// <summary>Читает пользователя с блокировкой строки: сериализует операции над его счетами. Вызывать в транзакции.</summary>
    Task<User?> FindByIdForUpdateAsync(Guid id, CancellationToken ct);

    Task<User?> FindByEmailAsync(string normalizedEmail, CancellationToken ct);

    /// <summary>Читает пользователя с блокировкой строки (<c>FOR UPDATE</c>): сериализует операции над ним. Вызывать в транзакции.</summary>
    Task<User?> FindByEmailForUpdateAsync(string normalizedEmail, CancellationToken ct);

    /// <summary>Читает токен с блокировкой строки: параллельное подтверждение одним токеном выстраивается в очередь. Вызывать в транзакции.</summary>
    Task<EmailConfirmationToken?> FindTokenByHashAsync(string hash, CancellationToken ct);

    Task<EmailConfirmationToken?> FindLatestTokenAsync(Guid userId, CancellationToken ct);

    /// <summary>Помечает использованными все неиспользованные токены пользователя.</summary>
    Task InvalidateOpenTokensAsync(Guid userId, DateTimeOffset now, CancellationToken ct);

    /// <summary>Читает токен сброса с блокировкой строки: параллельный сброс одним токеном выстраивается в очередь. Вызывать в транзакции.</summary>
    Task<PasswordResetToken?> FindResetTokenByHashAsync(string hash, CancellationToken ct);

    Task<PasswordResetToken?> FindLatestResetTokenAsync(Guid userId, CancellationToken ct);

    /// <summary>Помечает использованными все неиспользованные токены сброса пользователя.</summary>
    Task InvalidateOpenResetTokensAsync(Guid userId, DateTimeOffset now, CancellationToken ct);

    void Add(User user);

    void Add(EmailConfirmationToken token);

    void Add(PasswordResetToken token);
}
