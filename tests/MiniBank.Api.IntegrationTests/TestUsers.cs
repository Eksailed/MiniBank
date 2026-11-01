using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace MiniBank.Api.IntegrationTests;

internal static class TestUsers
{
    public const string DefaultPassword = "correct-horse-1";

    /// <summary>Регистрирует пользователя и подтверждает email по ссылке из письма. Возвращает email.</summary>
    public static async Task<string> RegisterAndConfirmAsync(
        ApiFactory factory, string? email = null, string password = DefaultPassword, string name = "Alice")
    {
        email ??= $"user-{Guid.NewGuid():N}@example.com";
        using var client = factory.CreateClient();

        var register = await client.PostAsJsonAsync("/auth/register", new { email, password, displayName = name });
        Assert.Equal(HttpStatusCode.Accepted, register.StatusCode);

        var confirm = await client.PostAsJsonAsync(
            "/auth/confirm-email", new { token = factory.Email.LastConfirmationToken(email) });
        Assert.Equal(HttpStatusCode.NoContent, confirm.StatusCode);
        return email;
    }

    /// <summary>Входит под уже подтверждённым пользователем: клиент с заголовком <c>Authorization: Bearer</c>.</summary>
    public static async Task<HttpClient> SignInAsync(ApiFactory factory, string email, string password = DefaultPassword)
    {
        var client = factory.CreateClient();
        var login = await client.PostAsJsonAsync("/auth/login", new { email, password });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var body = await login.Content.ReadFromJsonAsync<LoginBody>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", body!.AccessToken);
        return client;
    }

    /// <summary>Регистрирует нового пользователя, подтверждает email и входит.</summary>
    public static async Task<HttpClient> NewSignedInClientAsync(
        ApiFactory factory, string password = DefaultPassword, string name = "Alice")
    {
        var email = await RegisterAndConfirmAsync(factory, password: password, name: name);
        return await SignInAsync(factory, email, password);
    }

    public sealed record LoginBody(string AccessToken, DateTimeOffset ExpiresAt);
}
