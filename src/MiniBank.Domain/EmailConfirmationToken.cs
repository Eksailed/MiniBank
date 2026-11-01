namespace MiniBank.Domain;

public sealed class EmailConfirmationToken
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(24);

    private EmailConfirmationToken()
    {
        TokenHash = string.Empty;
    }

    public Guid Id { get; private set; }
    public Guid UserId { get; private set; }
    public string TokenHash { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset ExpiresAt { get; private set; }
    public DateTimeOffset? UsedAt { get; private set; }

    public static EmailConfirmationToken Create(Guid userId, string tokenHash, DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(),
        UserId = userId,
        TokenHash = tokenHash,
        CreatedAt = now,
        ExpiresAt = now + Lifetime
    };

    public bool IsUsableAt(DateTimeOffset now) => UsedAt is null && now < ExpiresAt;

    public void MarkUsed(DateTimeOffset now) => UsedAt = now;
}
