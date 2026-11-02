using MiniBank.Domain;

namespace MiniBank.Domain.Tests;

public class UserTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Register_NormalizesEmailAndTrimsName_AndStartsUnconfirmed()
    {
        var user = User.Register(" Alice@Example.com ", "hash", "  Alice  ", Now);

        Assert.Equal("alice@example.com", user.Email);
        Assert.Equal("Alice", user.DisplayName);
        Assert.Equal("hash", user.PasswordHash);
        Assert.Equal(Now, user.CreatedAt);
        Assert.NotEqual(Guid.Empty, user.Id);
        Assert.False(user.IsEmailConfirmed);
        Assert.Null(user.EmailConfirmedAt);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Register_RejectsEmptyName(string? name)
    {
        Assert.Throws<DomainValidationException>(() => User.Register("a@b.c", "hash", name, Now));
    }

    [Fact]
    public void Register_RejectsNameLongerThan100()
    {
        User.Register("a@b.c", "hash", new string('x', 100), Now);
        Assert.Throws<DomainValidationException>(() => User.Register("a@b.c", "hash", new string('x', 101), Now));
    }

    [Fact]
    public void ConfirmEmail_SetsConfirmedAt()
    {
        var user = User.Register("a@b.c", "hash", "Alice", Now);

        user.ConfirmEmail(Now.AddMinutes(5));

        Assert.True(user.IsEmailConfirmed);
        Assert.Equal(Now.AddMinutes(5), user.EmailConfirmedAt);
    }

    [Fact]
    public void ChangePassword_ReplacesHashAndSetsPasswordChangedAt()
    {
        var user = User.Register("a@b.c", "old", "Alice", Now);

        user.ChangePassword("new", Now.AddMinutes(5));

        Assert.Equal("new", user.PasswordHash);
        Assert.Equal(Now.AddMinutes(5), user.PasswordChangedAt);
    }

    [Fact]
    public void ChangePassword_ConfirmsEmailOfUnconfirmedUser()
    {
        var user = User.Register("a@b.c", "old", "Alice", Now);

        user.ChangePassword("new", Now.AddMinutes(5));

        Assert.True(user.IsEmailConfirmed);
        Assert.Equal(Now.AddMinutes(5), user.EmailConfirmedAt);
    }

    [Fact]
    public void ChangePassword_KeepsOriginalConfirmationTimeOfConfirmedUser()
    {
        var user = User.Register("a@b.c", "old", "Alice", Now);
        user.ConfirmEmail(Now.AddMinutes(1));

        user.ChangePassword("new", Now.AddMinutes(5));

        Assert.Equal(Now.AddMinutes(1), user.EmailConfirmedAt);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ChangePassword_RejectsBlankHash(string hash)
    {
        var user = User.Register("a@b.c", "old", "Alice", Now);

        Assert.Throws<DomainValidationException>(() => user.ChangePassword(hash, Now));
    }

    [Fact]
    public void IsSessionRevoked_FalseWhenPasswordNeverChanged()
    {
        var user = User.Register("a@b.c", "hash", "Alice", Now);

        Assert.False(user.IsSessionRevoked(Now.AddYears(-1)));
    }

    [Fact]
    public void IsSessionRevoked_TrueForTokenIssuedBeforeChange_FalseForTokenIssuedAtOrAfter()
    {
        var user = User.Register("a@b.c", "old", "Alice", Now);
        var changedAt = new DateTimeOffset(2026, 11, 1, 12, 0, 30, 700, TimeSpan.Zero);
        user.ChangePassword("new", changedAt);

        Assert.True(user.IsSessionRevoked(new DateTimeOffset(2026, 11, 1, 12, 0, 29, TimeSpan.Zero)));
        Assert.False(user.IsSessionRevoked(new DateTimeOffset(2026, 11, 1, 12, 0, 30, TimeSpan.Zero)));
        Assert.False(user.IsSessionRevoked(new DateTimeOffset(2026, 11, 1, 12, 0, 31, TimeSpan.Zero)));
    }
}
