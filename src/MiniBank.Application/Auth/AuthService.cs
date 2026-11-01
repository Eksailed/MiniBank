using Microsoft.Extensions.Logging;
using MiniBank.Application.Abstractions;
using MiniBank.Domain;

namespace MiniBank.Application.Auth;

public sealed class AuthService(
    IUserRepository users,
    IUnitOfWork unitOfWork,
    IPasswordHasher hasher,
    IAccessTokenIssuer tokenIssuer,
    IEmailSender emailSender,
    AuthOptions options,
    TimeProvider clock,
    ILogger<AuthService> logger)
{
    /// <summary>Имя уникального индекса из конфигурации EF (<c>UserConfiguration</c>).</summary>
    private const string EmailIndex = "ix_users_email";

    private static readonly TimeSpan ResendInterval = TimeSpan.FromSeconds(60);

    /// <exception cref="DomainValidationException">Email, пароль или имя не прошли проверку.</exception>
    /// <exception cref="EmailTakenException">Email уже занят.</exception>
    public async Task RegisterAsync(string? email, string? password, string? displayName, CancellationToken ct)
    {
        var normalizedEmail = EmailAddress.Normalize(email);
        PasswordPolicy.Validate(password);

        var now = clock.GetUtcNow();
        var user = User.Register(normalizedEmail, hasher.Hash(password!), displayName, now);
        var (raw, tokenHash) = ConfirmationTokens.Generate();

        try
        {
            await unitOfWork.ExecuteInTransactionAsync(async innerCt =>
            {
                users.Add(user);
                users.Add(EmailConfirmationToken.Create(user.Id, tokenHash, now));
                await unitOfWork.SaveChangesAsync(innerCt);
                return 0;
            }, ct);
        }
        catch (UniqueViolationException ex) when (ex.ConstraintName == EmailIndex)
        {
            throw new EmailTakenException();
        }

        await SendConfirmationAsync(user.Id, user.Email, user.DisplayName, raw, ct);
    }

    /// <exception cref="InvalidConfirmationTokenException">Токен неизвестен, истёк или уже использован.</exception>
    public async Task ConfirmEmailAsync(string? token, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidConfirmationTokenException();

        var hash = ConfirmationTokens.Hash(token.Trim());

        await unitOfWork.ExecuteInTransactionAsync(async innerCt =>
        {
            var now = clock.GetUtcNow();
            var stored = await users.FindTokenByHashAsync(hash, innerCt);
            if (stored is null || !stored.IsUsableAt(now))
                throw new InvalidConfirmationTokenException();

            var user = await users.FindByIdAsync(stored.UserId, innerCt)
                ?? throw new InvalidConfirmationTokenException();

            if (!user.IsEmailConfirmed)
                user.ConfirmEmail(now);
            await users.InvalidateOpenTokensAsync(user.Id, now, innerCt);
            await unitOfWork.SaveChangesAsync(innerCt);
            return 0;
        }, ct);
    }

    /// <summary>
    /// Отправляет новое письмо. Никогда не сообщает, есть ли такой пользователь и подтверждён ли он:
    /// неизвестный, подтверждённый или слишком частый запрос просто ничего не делает.
    /// </summary>
    public async Task ResendConfirmationAsync(string? email, CancellationToken ct)
    {
        if (!TryNormalizeEmail(email, out var normalizedEmail))
            return;

        var outgoing = await unitOfWork.ExecuteInTransactionAsync<OutgoingConfirmation?>(async innerCt =>
        {
            var now = clock.GetUtcNow();
            var user = await users.FindByEmailForUpdateAsync(normalizedEmail, innerCt);
            if (user is null || user.IsEmailConfirmed)
                return null;

            var latest = await users.FindLatestTokenAsync(user.Id, innerCt);
            if (latest is not null && now - latest.CreatedAt < ResendInterval)
                return null;

            await users.InvalidateOpenTokensAsync(user.Id, now, innerCt);
            var (raw, hash) = ConfirmationTokens.Generate();
            users.Add(EmailConfirmationToken.Create(user.Id, hash, now));
            await unitOfWork.SaveChangesAsync(innerCt);
            return new OutgoingConfirmation(user.Id, user.Email, user.DisplayName, raw);
        }, ct);

        if (outgoing is not null)
            await SendConfirmationAsync(outgoing.UserId, outgoing.Email, outgoing.DisplayName, outgoing.RawToken, ct);
    }

    /// <exception cref="InvalidCredentialsException">Неверный email или пароль (ответ один и тот же в обоих случаях).</exception>
    /// <exception cref="EmailNotConfirmedException">Пароль верный, но email не подтверждён.</exception>
    public async Task<AccessToken> LoginAsync(string? email, string? password, CancellationToken ct)
    {
        var user = TryNormalizeEmail(email, out var normalizedEmail)
            ? await users.FindByEmailAsync(normalizedEmail, ct)
            : null;

        // Проверка идёт всегда, в том числе по DummyHash для неизвестного email: время ответа не выдаёт, есть ли пользователь.
        // Слишком длинный пароль заведомо неверен и не должен заставлять сервер считать PBKDF2 над мегабайтами.
        var candidate = password is { Length: <= PasswordPolicy.MaxLength } ? password : string.Empty;
        var passwordMatches = hasher.Verify(user?.PasswordHash ?? hasher.DummyHash, candidate);

        if (user is null || !passwordMatches)
            throw new InvalidCredentialsException();
        if (!user.IsEmailConfirmed)
            throw new EmailNotConfirmedException();

        return tokenIssuer.Issue(user, clock.GetUtcNow());
    }

    /// <returns><c>null</c>, если пользователя с таким id нет (например, токен выдан до удаления учётной записи).</returns>
    public async Task<UserProfileDto?> GetProfileAsync(Guid userId, CancellationToken ct)
    {
        var user = await users.FindByIdAsync(userId, ct);
        return user is null ? null : new UserProfileDto(user.Id, user.Email, user.DisplayName);
    }

    private static bool TryNormalizeEmail(string? email, out string normalized)
    {
        try
        {
            normalized = EmailAddress.Normalize(email);
            return true;
        }
        catch (DomainValidationException)
        {
            normalized = string.Empty;
            return false;
        }
    }

    /// <summary>Сбой отправки не откатывает регистрацию: пользователь может запросить письмо повторно.</summary>
    private async Task SendConfirmationAsync(Guid userId, string email, string displayName, string rawToken, CancellationToken ct)
    {
        var link = $"{options.WebBaseUrl}/confirm-email#token={rawToken}";
        var message = new EmailMessage(
            email,
            "Подтвердите email в MiniBank",
            $"Здравствуйте, {displayName}!\n\n" +
            $"Чтобы подтвердить email в MiniBank, откройте ссылку:\n{link}\n\n" +
            "Ссылка действует 24 часа. Если вы не регистрировались, просто проигнорируйте это письмо.\n");

        try
        {
            await emailSender.SendAsync(message, ct);
        }
        catch (Exception ex)
        {
            // В лог — без токена, ссылки и адреса: письмо содержит секрет, адрес — персональные данные.
            logger.LogWarning("Не удалось отправить письмо подтверждения пользователю {UserId}: {ErrorType}",
                userId, ex.GetType().Name);
        }
    }

    private sealed record OutgoingConfirmation(Guid UserId, string Email, string DisplayName, string RawToken);
}
