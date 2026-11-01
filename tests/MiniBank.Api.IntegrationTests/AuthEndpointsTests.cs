using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using MiniBank.TestSupport;

namespace MiniBank.Api.IntegrationTests;

[Collection("postgres")]
public class AuthEndpointsTests(PostgresFixture postgres)
{
    private const string Password = TestUsers.DefaultPassword;

    private static async Task<JsonElement> ReadProblemAsync(HttpResponseMessage response)
    {
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).Clone();
    }

    private static string? Code(JsonElement problem) =>
        problem.TryGetProperty("code", out var code) ? code.GetString() : null;

    [Fact]
    public async Task FullFlow_Register_Confirm_Login_Me()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        using var client = factory.CreateClient();
        var email = "Alice@Example.com";

        var register = await client.PostAsJsonAsync("/auth/register", new { email, password = Password, displayName = "Alice" });
        Assert.Equal(HttpStatusCode.Accepted, register.StatusCode);
        var mail = Assert.Single(factory.Email.Sent);
        Assert.Equal("alice@example.com", mail.To);

        var early = await client.PostAsJsonAsync("/auth/login", new { email, password = Password });
        Assert.Equal(HttpStatusCode.Forbidden, early.StatusCode);
        Assert.Equal("email_not_confirmed", Code(await ReadProblemAsync(early)));

        var confirm = await client.PostAsJsonAsync("/auth/confirm-email", new { token = factory.Email.LastConfirmationToken("alice@example.com") });
        Assert.Equal(HttpStatusCode.NoContent, confirm.StatusCode);

        var login = await client.PostAsJsonAsync("/auth/login", new { email, password = Password });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var body = await login.Content.ReadFromJsonAsync<TestUsers.LoginBody>();
        Assert.False(string.IsNullOrEmpty(body!.AccessToken));
        Assert.True(body.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(55));

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", body.AccessToken);
        var me = await client.GetAsync("/me");
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        var profile = await me.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("alice@example.com", profile.GetProperty("email").GetString());
        Assert.Equal("Alice", profile.GetProperty("displayName").GetString());
        Assert.True(Guid.TryParse(profile.GetProperty("id").GetString(), out _));
    }

    [Fact]
    public async Task Register_DuplicateEmail_Returns409WithCode()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        using var client = factory.CreateClient();
        await client.PostAsJsonAsync("/auth/register", new { email = "dup@example.com", password = Password, displayName = "A" });

        var second = await client.PostAsJsonAsync("/auth/register", new { email = "DUP@example.com", password = Password, displayName = "B" });

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal("email_taken", Code(await ReadProblemAsync(second)));
    }

    [Theory]
    [InlineData("not-an-email", "correct-horse-1", "Alice")]
    [InlineData("a@example.com", "1234567", "Alice")]
    [InlineData("a@example.com", "correct-horse-1", "")]
    [InlineData(null, "correct-horse-1", "Alice")]
    [InlineData("a@example.com", null, "Alice")]
    public async Task Register_BadInput_Returns400(string? email, string? password, string? displayName)
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/auth/register", new { email, password, displayName });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Empty(factory.Email.Attempted);
    }

    [Fact]
    public async Task ConfirmEmail_BadToken_Returns400WithCode()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/auth/confirm-email", new { token = "no-such-token" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_confirmation_token", Code(await ReadProblemAsync(response)));
    }

    [Fact]
    public async Task ConfirmEmail_ReusedToken_ReturnsTheSameError()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        using var client = factory.CreateClient();
        await client.PostAsJsonAsync("/auth/register", new { email = "once@example.com", password = Password, displayName = "A" });
        var token = factory.Email.LastConfirmationToken("once@example.com");

        var first = await client.PostAsJsonAsync("/auth/confirm-email", new { token });
        var second = await client.PostAsJsonAsync("/auth/confirm-email", new { token });

        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, second.StatusCode);
        Assert.Equal("invalid_confirmation_token", Code(await ReadProblemAsync(second)));
    }

    [Fact]
    public async Task Login_WrongPassword_And_UnknownEmail_ReturnIdentical401()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        var email = await TestUsers.RegisterAndConfirmAsync(factory);
        using var client = factory.CreateClient();

        var wrongPassword = await client.PostAsJsonAsync("/auth/login", new { email, password = "wrong-password-1" });
        var unknown = await client.PostAsJsonAsync("/auth/login", new { email = "nobody@example.com", password = "wrong-password-1" });

        Assert.Equal(HttpStatusCode.Unauthorized, wrongPassword.StatusCode);
        Assert.Equal(wrongPassword.StatusCode, unknown.StatusCode);
        var a = await ReadProblemAsync(wrongPassword);
        var b = await ReadProblemAsync(unknown);
        Assert.Equal("invalid_credentials", Code(a));
        Assert.Equal(Code(a), Code(b));
        Assert.Equal(a.GetProperty("title").GetString(), b.GetProperty("title").GetString());
        Assert.Equal(a.GetProperty("detail").GetString(), b.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Resend_AlwaysReturns202_AndOnlyUnconfirmedUserGetsMail()
    {
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        await using var factory = await ApiFactory.CreateAsync(postgres, configureServices: services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(clock);
        });
        using var client = factory.CreateClient();
        await client.PostAsJsonAsync("/auth/register", new { email = "pending@example.com", password = Password, displayName = "P" });
        var confirmed = await TestUsers.RegisterAndConfirmAsync(factory);
        var sentBefore = factory.Email.Sent.Count;
        clock.Advance(TimeSpan.FromSeconds(61));

        foreach (var email in new[] { "pending@example.com", "nobody@example.com", confirmed })
        {
            var response = await client.PostAsJsonAsync("/auth/resend-confirmation", new { email });
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        }

        var added = factory.Email.Sent.Skip(sentBefore).ToList();
        var mail = Assert.Single(added);
        Assert.Equal("pending@example.com", mail.To);
    }

    [Fact]
    public async Task Me_WithoutToken_Returns401ProblemJson()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/me");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Me_WithForgedOrBrokenTokens_Returns401_AndHandBuiltValidTokenWorks()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        var email = await TestUsers.RegisterAndConfirmAsync(factory);
        using var real = await TestUsers.SignInAsync(factory, email);
        var userId = new JsonWebToken(real.DefaultRequestHeaders.Authorization!.Parameter!).Subject;
        var now = DateTime.UtcNow;

        string Build(string key = ApiFactory.JwtKey, string issuer = "minibank", string audience = "minibank-web",
            DateTime? expires = null, DateTime? notBefore = null)
        {
            var descriptor = new SecurityTokenDescriptor
            {
                Subject = new ClaimsIdentity([new Claim("sub", userId)]),
                Issuer = issuer,
                Audience = audience,
                NotBefore = notBefore ?? now.AddMinutes(-10),
                Expires = expires ?? now.AddMinutes(10),
                SigningCredentials = new SigningCredentials(
                    new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key)), SecurityAlgorithms.HmacSha256)
            };
            return new JsonWebTokenHandler().CreateToken(descriptor);
        }

        static string Base64Url(string json) => Base64UrlEncoder.Encode(Encoding.UTF8.GetBytes(json));
        var noneToken = $"{Base64Url("{\"alg\":\"none\",\"typ\":\"JWT\"}")}." +
            $"{Base64Url($"{{\"sub\":\"{userId}\",\"iss\":\"minibank\",\"aud\":\"minibank-web\",\"exp\":{DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeSeconds()}}}")}.";

        async Task<HttpStatusCode> MeWithAsync(string token)
        {
            using var client = factory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return (await client.GetAsync("/me")).StatusCode;
        }

        Assert.Equal(HttpStatusCode.OK, await MeWithAsync(Build()));
        Assert.Equal(HttpStatusCode.Unauthorized, await MeWithAsync(Build(expires: now.AddMinutes(-1))));
        Assert.Equal(HttpStatusCode.Unauthorized, await MeWithAsync(Build(issuer: "someone-else")));
        Assert.Equal(HttpStatusCode.Unauthorized, await MeWithAsync(Build(audience: "another-app")));
        Assert.Equal(HttpStatusCode.Unauthorized, await MeWithAsync(Build(key: "a-completely-different-signing-key-9876543210")));
        Assert.Equal(HttpStatusCode.Unauthorized, await MeWithAsync(noneToken));
        Assert.Equal(HttpStatusCode.Unauthorized, await MeWithAsync("not-a-jwt"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("0123456789012345678901234567890")] // 31 байт: на единицу меньше порога
    public async Task Startup_WithMissingOrShortJwtKey_Throws(string key)
    {
        await using var factory = await ApiFactory.CreateAsync(
            postgres, settings: new Dictionary<string, string?> { ["Jwt:Key"] = key });

        var ex = Record.Exception(() => factory.CreateClient());

        Assert.NotNull(ex);
        Assert.Contains("Jwt:Key", Flatten(ex));
    }

    private static string Flatten(Exception ex)
    {
        var builder = new StringBuilder();
        for (var current = ex; current is not null; current = current.InnerException)
            builder.AppendLine(current.Message);
        return builder.ToString();
    }

    private async Task<int> LoginAttemptsUntilLimitAsync(ApiFactory factory, Func<int, string?> forwardedFor, int attempts)
    {
        using var client = factory.CreateClient();
        var first429 = 0;
        for (var i = 1; i <= attempts; i++)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/auth/login")
            {
                Content = JsonContent.Create(new { email = "nobody@example.com", password = Password })
            };
            if (forwardedFor(i) is { } ip)
                request.Headers.Add("X-Forwarded-For", ip);
            var response = await client.SendAsync(request);
            if (response.StatusCode == HttpStatusCode.TooManyRequests && first429 == 0)
            {
                first429 = i;
                Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
            }
            else
            {
                Assert.True(first429 != 0 || response.StatusCode == HttpStatusCode.Unauthorized,
                    $"Попытка {i}: ожидался 401, получен {(int)response.StatusCode}.");
            }
        }
        return first429;
    }

    private static readonly IReadOnlyDictionary<string, string?> ThreePerMinute =
        new Dictionary<string, string?> { ["Auth:RateLimitPerMinute"] = "3" };

    [Fact]
    public async Task Swagger_Document_DeclaresBearerScheme()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres, environment: "Development");

        var json = await factory.CreateClient().GetStringAsync("/swagger/v1/swagger.json");

        using var document = JsonDocument.Parse(json);
        var scheme = document.RootElement.GetProperty("components").GetProperty("securitySchemes").GetProperty("Bearer");
        Assert.Equal("bearer", scheme.GetProperty("scheme").GetString());
    }

    [Fact]
    public async Task RateLimit_FourthLoginFromOneAddress_Returns429ProblemJson()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres, settings: ThreePerMinute);

        var first429 = await LoginAttemptsUntilLimitAsync(factory, _ => null, attempts: 4);

        Assert.Equal(4, first429);
    }

    [Fact]
    public async Task RateLimit_ForgedForwardedFor_WithoutTrustedNetwork_DoesNotBypassLimit()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres, settings: ThreePerMinute);

        var first429 = await LoginAttemptsUntilLimitAsync(factory, i => $"203.0.113.{i}", attempts: 4);

        Assert.Equal(4, first429);
    }

    [Fact]
    public async Task RateLimit_WithTrustedNetwork_CountsForwardedClientsSeparately()
    {
        var settings = new Dictionary<string, string?>
        {
            ["Auth:RateLimitPerMinute"] = "3",
            ["ForwardedHeaders:TrustedNetworks:0"] = "10.0.0.0/8"
        };
        await using var factory = await ApiFactory.CreateAsync(postgres, settings: settings,
            configureServices: services => services.AddSingleton<IStartupFilter, ProxyAddressFilter>());

        var distinct = await LoginAttemptsUntilLimitAsync(factory, i => $"203.0.113.{i}", attempts: 6);
        var same = await LoginAttemptsUntilLimitAsync(factory, _ => "198.51.100.7", attempts: 4);

        Assert.Equal(0, distinct);
        Assert.Equal(4, same);
    }

    /// <summary>Подставляет адрес прокси 10.0.0.1 как адрес соединения: TestServer иначе отдаёт его пустым.</summary>
    private sealed class ProxyAddressFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, nextMiddleware) =>
            {
                context.Connection.RemoteIpAddress = IPAddress.Parse("10.0.0.1");
                return nextMiddleware(context);
            });
            next(app);
        };
    }
}
