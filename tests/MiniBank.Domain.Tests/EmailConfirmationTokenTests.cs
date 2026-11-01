using MiniBank.Domain;

namespace MiniBank.Domain.Tests;

public class EmailConfirmationTokenTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Create_ExpiresIn24Hours()
    {
        var userId = Guid.NewGuid();

        var token = EmailConfirmationToken.Create(userId, "hash", Now);

        Assert.Equal(userId, token.UserId);
        Assert.Equal("hash", token.TokenHash);
        Assert.Equal(Now, token.CreatedAt);
        Assert.Equal(Now.AddHours(24), token.ExpiresAt);
        Assert.Null(token.UsedAt);
    }

    [Fact]
    public void IsUsableAt_TrueBeforeExpiry_FalseAtAndAfter()
    {
        var token = EmailConfirmationToken.Create(Guid.NewGuid(), "hash", Now);

        Assert.True(token.IsUsableAt(Now.AddHours(24).AddTicks(-1)));
        Assert.False(token.IsUsableAt(Now.AddHours(24)));
        Assert.False(token.IsUsableAt(Now.AddHours(25)));
    }

    [Fact]
    public void MarkUsed_MakesTokenUnusable()
    {
        var token = EmailConfirmationToken.Create(Guid.NewGuid(), "hash", Now);

        token.MarkUsed(Now.AddMinutes(1));

        Assert.Equal(Now.AddMinutes(1), token.UsedAt);
        Assert.False(token.IsUsableAt(Now.AddMinutes(2)));
    }
}
