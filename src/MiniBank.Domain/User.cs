namespace MiniBank.Domain;

public sealed class User
{
    public const int MaxDisplayNameLength = 100;

    private User()
    {
        Email = string.Empty;
        PasswordHash = string.Empty;
        DisplayName = string.Empty;
    }

    public Guid Id { get; private set; }
    public string Email { get; private set; }
    public string PasswordHash { get; private set; }
    public string DisplayName { get; private set; }
    public DateTimeOffset? EmailConfirmedAt { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    public bool IsEmailConfirmed => EmailConfirmedAt is not null;

    public static User Register(string? email, string passwordHash, string? displayName, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(displayName))
            throw new DomainValidationException("Display name must not be empty.");
        if (displayName.Trim().Length > MaxDisplayNameLength)
            throw new DomainValidationException($"Display name must be at most {MaxDisplayNameLength} characters.");
        if (displayName.Any(char.IsControl))
            throw new DomainValidationException("Display name must not contain control characters.");

        return new User
        {
            Id = Guid.NewGuid(),
            Email = EmailAddress.Normalize(email),
            PasswordHash = passwordHash,
            DisplayName = displayName.Trim(),
            CreatedAt = now
        };
    }

    public void ConfirmEmail(DateTimeOffset now) => EmailConfirmedAt = now;
}
