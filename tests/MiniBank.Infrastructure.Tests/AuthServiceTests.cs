using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MiniBank.Application;
using MiniBank.Application.Abstractions;
using MiniBank.Application.Auth;
using MiniBank.Domain;
using MiniBank.Infrastructure.Persistence;
using MiniBank.TestSupport;

namespace MiniBank.Infrastructure.Tests;

[Collection("postgres")]
public class AuthServiceTests(PostgresFixture postgres)
{
    private const string Password = "correct-horse-1";
    private static readonly DateTimeOffset Start = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

    private sealed class FixedTokenIssuer : IAccessTokenIssuer
    {
        public AccessToken Issue(User user, DateTimeOffset now) => new("fixed-token", now.AddHours(1));
    }

    private sealed class CountingHasher(IPasswordHasher inner) : IPasswordHasher
    {
        private readonly object _gate = new();
        private readonly List<string> _verifiedHashes = [];

        public IReadOnlyList<string> VerifiedHashes
        {
            get { lock (_gate) return [.. _verifiedHashes]; }
        }

        public string DummyHash => inner.DummyHash;

        public string Hash(string password) => inner.Hash(password);

        public bool Verify(string hash, string password)
        {
            lock (_gate) _verifiedHashes.Add(hash);
            return inner.Verify(hash, password);
        }
    }

    private sealed class CollectingLogger<T> : ILogger<T>
    {
        private readonly object _gate = new();
        private readonly List<string> _messages = [];

        public IReadOnlyList<string> Messages
        {
            get { lock (_gate) return [.. _messages]; }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (_gate) _messages.Add(formatter(state, exception) + exception);
        }
    }

    private sealed class Harness(
        ServiceProvider provider, FakeEmailSender email, FakeClock clock, CountingHasher hasher, CollectingLogger<AuthService> log)
    {
        public FakeEmailSender Email { get; } = email;
        public FakeClock Clock { get; } = clock;
        public CountingHasher Hasher { get; } = hasher;
        public CollectingLogger<AuthService> Log { get; } = log;

        public async Task<T> WithAsync<T>(Func<IServiceProvider, Task<T>> action)
        {
            await using var scope = provider.CreateAsyncScope();
            return await action(scope.ServiceProvider);
        }

        public Task RegisterAsync(string email, string? password = Password, string? name = "Alice") =>
            WithAsync(async sp =>
            {
                await sp.GetRequiredService<AuthService>().RegisterAsync(email, password, name, default);
                return 0;
            });

        public Task ConfirmAsync(string? token) =>
            WithAsync(async sp =>
            {
                await sp.GetRequiredService<AuthService>().ConfirmEmailAsync(token, default);
                return 0;
            });

        public Task ResendAsync(string? email) =>
            WithAsync(async sp =>
            {
                await sp.GetRequiredService<AuthService>().ResendConfirmationAsync(email, default);
                return 0;
            });

        public Task<AccessToken> LoginAsync(string? email, string? password) =>
            WithAsync(sp => sp.GetRequiredService<AuthService>().LoginAsync(email, password, default));

        public Task<T> QueryAsync<T>(Func<MiniBankDbContext, Task<T>> query) =>
            WithAsync(sp => query(sp.GetRequiredService<MiniBankDbContext>()));

        public async Task RegisterAndConfirmAsync(string email)
        {
            await RegisterAsync(email);
            await ConfirmAsync(Email.LastConfirmationToken(email));
        }
    }

    private async Task<Harness> CreateAsync()
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        var email = new FakeEmailSender();
        var clock = new FakeClock(Start);
        var log = new CollectingLogger<AuthService>();

        var services = new ServiceCollection().AddInfrastructure(connectionString).AddApplication();
        var realHasher = services.BuildServiceProvider().GetRequiredService<IPasswordHasher>();
        var hasher = new CountingHasher(realHasher);
        services.AddScoped<AuthService>();
        services.AddSingleton(new AuthOptions { WebBaseUrl = "https://app.test" });
        services.AddSingleton<IEmailSender>(email);
        services.AddSingleton<IPasswordHasher>(hasher);
        services.AddSingleton<IAccessTokenIssuer, FixedTokenIssuer>();
        services.AddSingleton<TimeProvider>(clock);
        services.AddSingleton<ILogger<AuthService>>(log);
        var provider = services.BuildServiceProvider();

        await using (var scope = provider.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<MiniBankDbContext>().Database.MigrateAsync();

        return new Harness(provider, email, clock, hasher, log);
    }

    [Fact]
    public async Task Register_CreatesUnconfirmedUser_AndSendsOneEmailWithLink()
    {
        var h = await CreateAsync();

        await h.RegisterAsync(" Alice@Example.com ");

        var message = Assert.Single(h.Email.Sent);
        var token = h.Email.LastConfirmationToken("alice@example.com");
        Assert.Equal("alice@example.com", message.To);
        Assert.Equal("Подтвердите email в MiniBank", message.Subject);
        Assert.Contains($"https://app.test/confirm-email?token={token}", message.Body);

        var (user, tokens) = await h.QueryAsync(async db => (await db.Users.SingleAsync(), await db.EmailConfirmationTokens.ToListAsync()));
        Assert.Equal("alice@example.com", user.Email);
        Assert.Equal("Alice", user.DisplayName);
        Assert.False(user.IsEmailConfirmed);
        Assert.NotEqual(Password, user.PasswordHash);
        var stored = Assert.Single(tokens);
        Assert.Equal(user.Id, stored.UserId);
        Assert.Equal(ConfirmationTokens.Hash(token), stored.TokenHash);
        Assert.NotEqual(token, stored.TokenHash);
        Assert.Equal(Start.AddHours(24), stored.ExpiresAt);
    }

    [Fact]
    public async Task Register_DuplicateEmailDifferentCaseAndSpaces_ThrowsEmailTaken()
    {
        var h = await CreateAsync();
        await h.RegisterAsync("a@b.co");

        await Assert.ThrowsAsync<EmailTakenException>(() => h.RegisterAsync("  A@B.CO "));

        Assert.Equal(1, await h.QueryAsync(db => db.Users.CountAsync()));
        Assert.Single(h.Email.Sent);
    }

    [Fact]
    public async Task Register_ParallelSameEmail_ExactlyOneSucceeds()
    {
        var h = await CreateAsync();

        var results = await Task.WhenAll(Enumerable.Range(0, 10).Select(async _ =>
        {
            try
            {
                await Task.Run(() => h.RegisterAsync("race@b.co"));
                return (Exception?)null;
            }
            catch (Exception ex)
            {
                return ex;
            }
        }));

        Assert.Equal(1, results.Count(r => r is null));
        Assert.All(results.Where(r => r is not null), r => Assert.IsType<EmailTakenException>(r));
        Assert.Equal(1, await h.QueryAsync(db => db.Users.CountAsync()));
        Assert.Single(h.Email.Sent);
    }

    [Fact]
    public async Task Register_SmtpFails_StillSucceeds_AndLogsWithoutToken()
    {
        var h = await CreateAsync();
        h.Email.FailWith = new InvalidOperationException("smtp down");

        await h.RegisterAsync("alice@example.com");

        Assert.Equal(1, await h.QueryAsync(db => db.Users.CountAsync()));
        var token = h.Email.LastConfirmationToken("alice@example.com");
        var logged = h.Log.Messages;
        Assert.NotEmpty(logged);
        Assert.All(logged, m =>
        {
            Assert.DoesNotContain(token, m);
            Assert.DoesNotContain("confirm-email", m);
            Assert.DoesNotContain("alice@example.com", m);
        });
    }

    [Fact]
    public async Task Register_InvalidInput_ThrowsDomainValidation()
    {
        var h = await CreateAsync();

        await Assert.ThrowsAsync<DomainValidationException>(() => h.RegisterAsync("not-an-email"));
        await Assert.ThrowsAsync<DomainValidationException>(() => h.RegisterAsync("a@b.co", "1234567"));
        await Assert.ThrowsAsync<DomainValidationException>(() => h.RegisterAsync("a@b.co", null));
        await Assert.ThrowsAsync<DomainValidationException>(() => h.RegisterAsync("a@b.co", Password, "  "));

        Assert.Equal(0, await h.QueryAsync(db => db.Users.CountAsync()));
        Assert.Empty(h.Email.Attempted);
    }

    [Fact]
    public async Task ConfirmEmail_ValidToken_Confirms_AndTokenIsSingleUse()
    {
        var h = await CreateAsync();
        await h.RegisterAsync("a@b.co");
        var token = h.Email.LastConfirmationToken("a@b.co");
        h.Clock.Advance(TimeSpan.FromMinutes(5));

        await h.ConfirmAsync(token);

        var user = await h.QueryAsync(db => db.Users.SingleAsync());
        Assert.True(user.IsEmailConfirmed);
        Assert.Equal(Start.AddMinutes(5), user.EmailConfirmedAt);
        await Assert.ThrowsAsync<InvalidConfirmationTokenException>(() => h.ConfirmAsync(token));
    }

    [Fact]
    public async Task ConfirmEmail_MarksAllUserTokensUsed()
    {
        var h = await CreateAsync();
        await h.RegisterAsync("a@b.co");
        var token = h.Email.LastConfirmationToken("a@b.co");
        await h.QueryAsync(async db =>
        {
            var user = await db.Users.SingleAsync();
            db.EmailConfirmationTokens.Add(EmailConfirmationToken.Create(user.Id, ConfirmationTokens.Hash("other"), Start));
            await db.SaveChangesAsync();
            return 0;
        });

        await h.ConfirmAsync(token);

        var tokens = await h.QueryAsync(db => db.EmailConfirmationTokens.ToListAsync());
        Assert.Equal(2, tokens.Count);
        Assert.All(tokens, t => Assert.NotNull(t.UsedAt));
        await Assert.ThrowsAsync<InvalidConfirmationTokenException>(() => h.ConfirmAsync("other"));
    }

    [Fact]
    public async Task ConfirmEmail_ExpiredAtExactlyTwentyFourHours_Throws()
    {
        var h = await CreateAsync();
        await h.RegisterAsync("a@b.co");
        var token = h.Email.LastConfirmationToken("a@b.co");
        h.Clock.Advance(TimeSpan.FromHours(24));

        await Assert.ThrowsAsync<InvalidConfirmationTokenException>(() => h.ConfirmAsync(token));

        Assert.False((await h.QueryAsync(db => db.Users.SingleAsync())).IsEmailConfirmed);
    }

    [Fact]
    public async Task ConfirmEmail_JustBeforeExpiry_Confirms()
    {
        var h = await CreateAsync();
        await h.RegisterAsync("a@b.co");
        var token = h.Email.LastConfirmationToken("a@b.co");
        h.Clock.Advance(TimeSpan.FromHours(24) - TimeSpan.FromSeconds(1));

        await h.ConfirmAsync(token);

        Assert.True((await h.QueryAsync(db => db.Users.SingleAsync())).IsEmailConfirmed);
    }

    [Theory]
    [InlineData("unknown-token")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task ConfirmEmail_UnknownOrEmptyToken_Throws(string? token)
    {
        var h = await CreateAsync();

        await Assert.ThrowsAsync<InvalidConfirmationTokenException>(() => h.ConfirmAsync(token));
    }

    [Fact]
    public async Task ConfirmEmail_ParallelSameToken_ExactlyOneSucceeds()
    {
        var h = await CreateAsync();
        await h.RegisterAsync("a@b.co");
        var token = h.Email.LastConfirmationToken("a@b.co");

        var results = await Task.WhenAll(Enumerable.Range(0, 5).Select(async _ =>
        {
            try
            {
                await Task.Run(() => h.ConfirmAsync(token));
                return (Exception?)null;
            }
            catch (Exception ex)
            {
                return ex;
            }
        }));

        Assert.Equal(1, results.Count(r => r is null));
        Assert.All(results.Where(r => r is not null), r => Assert.IsType<InvalidConfirmationTokenException>(r));
    }

    [Fact]
    public async Task Login_Confirmed_ReturnsToken()
    {
        var h = await CreateAsync();
        await h.RegisterAndConfirmAsync("a@b.co");
        h.Clock.Advance(TimeSpan.FromMinutes(10));

        var token = await h.LoginAsync(" A@B.co ", Password);

        Assert.Equal("fixed-token", token.Value);
        Assert.Equal(Start.AddMinutes(10).AddHours(1), token.ExpiresAt);
    }

    [Fact]
    public async Task Login_WrongPassword_And_UnknownEmail_ThrowSameException()
    {
        var h = await CreateAsync();
        await h.RegisterAndConfirmAsync("a@b.co");

        var wrongPassword = await Assert.ThrowsAsync<InvalidCredentialsException>(() => h.LoginAsync("a@b.co", "wrong-password"));
        var verifiedBefore = h.Hasher.VerifiedHashes.Count;
        var unknownEmail = await Assert.ThrowsAsync<InvalidCredentialsException>(() => h.LoginAsync("nobody@b.co", Password));

        Assert.Equal(wrongPassword.Message, unknownEmail.Message);
        Assert.Equal(verifiedBefore + 1, h.Hasher.VerifiedHashes.Count);
        Assert.Equal(h.Hasher.DummyHash, h.Hasher.VerifiedHashes[^1]);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("not-an-email", Password)]
    [InlineData("a@b.co", null)]
    [InlineData("", "")]
    public async Task Login_MalformedInput_ThrowsInvalidCredentials(string? email, string? password)
    {
        var h = await CreateAsync();
        await h.RegisterAndConfirmAsync("a@b.co");

        await Assert.ThrowsAsync<InvalidCredentialsException>(() => h.LoginAsync(email, password));
    }

    [Fact]
    public async Task Login_Unconfirmed_ThrowsEmailNotConfirmed_OnlyWithCorrectPassword()
    {
        var h = await CreateAsync();
        await h.RegisterAsync("a@b.co");

        await Assert.ThrowsAsync<EmailNotConfirmedException>(() => h.LoginAsync("a@b.co", Password));
        await Assert.ThrowsAsync<InvalidCredentialsException>(() => h.LoginAsync("a@b.co", "wrong-password"));
    }

    [Fact]
    public async Task Resend_Unconfirmed_InvalidatesOldTokens_AndSendsNew()
    {
        var h = await CreateAsync();
        await h.RegisterAsync("a@b.co");
        var oldToken = h.Email.LastConfirmationToken("a@b.co");
        h.Clock.Advance(TimeSpan.FromSeconds(61));

        await h.ResendAsync(" A@B.co ");

        Assert.Equal(2, h.Email.Sent.Count);
        var newToken = h.Email.LastConfirmationToken("a@b.co");
        Assert.NotEqual(oldToken, newToken);
        await Assert.ThrowsAsync<InvalidConfirmationTokenException>(() => h.ConfirmAsync(oldToken));
        await h.ConfirmAsync(newToken);
        Assert.True((await h.QueryAsync(db => db.Users.SingleAsync())).IsEmailConfirmed);
    }

    [Fact]
    public async Task Resend_WithinOneMinute_SendsNothing_AndSendsAtExactlyOneMinute()
    {
        var h = await CreateAsync();
        await h.RegisterAsync("a@b.co");

        h.Clock.Advance(TimeSpan.FromSeconds(59.9));
        await h.ResendAsync("a@b.co");
        Assert.Single(h.Email.Sent);

        h.Clock.Advance(TimeSpan.FromSeconds(0.1));
        await h.ResendAsync("a@b.co");
        Assert.Equal(2, h.Email.Sent.Count);
    }

    [Fact]
    public async Task Resend_ParallelRequests_SendExactlyOneEmail()
    {
        var h = await CreateAsync();
        await h.RegisterAsync("a@b.co");
        h.Clock.Advance(TimeSpan.FromSeconds(61));

        await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => Task.Run(() => h.ResendAsync("a@b.co"))));

        Assert.Equal(2, h.Email.Sent.Count);
        Assert.Equal(1, await h.QueryAsync(db => db.EmailConfirmationTokens.CountAsync(t => t.UsedAt == null)));
    }

    [Fact]
    public async Task Resend_UnknownOrConfirmedOrMalformed_SendsNothingAndDoesNotThrow()
    {
        var h = await CreateAsync();
        await h.RegisterAndConfirmAsync("confirmed@b.co");
        var sentBefore = h.Email.Sent.Count;
        h.Clock.Advance(TimeSpan.FromMinutes(5));

        await h.ResendAsync("nobody@b.co");
        await h.ResendAsync("confirmed@b.co");
        await h.ResendAsync("not-an-email");
        await h.ResendAsync(null);

        Assert.Equal(sentBefore, h.Email.Sent.Count);
    }

    [Fact]
    public async Task PasswordHasher_HashesWithSalt_AndVerifies()
    {
        var h = await CreateAsync();
        var hasher = h.Hasher;

        var first = hasher.Hash(Password);
        var second = hasher.Hash(Password);

        Assert.NotEqual(first, second);
        Assert.NotEqual(Password, first);
        Assert.True(hasher.Verify(first, Password));
        Assert.False(hasher.Verify(first, "wrong-password"));
        Assert.False(hasher.Verify(hasher.DummyHash, Password));
        Assert.Equal(hasher.DummyHash, hasher.DummyHash);
    }
}
