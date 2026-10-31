using System.Net.Http.Json;

namespace MiniBank.Api.IntegrationTests;

internal static class Requests
{
    public const string KeyHeader = "Idempotency-Key";

    /// <summary>POST с JSON-телом и заголовком Idempotency-Key (свежий Guid, если ключ не задан).</summary>
    public static Task<HttpResponseMessage> PostWithKeyAsync<T>(
        this HttpClient client, string url, T body, string? key = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
        request.Headers.Add(KeyHeader, key ?? Guid.NewGuid().ToString());
        return client.SendAsync(request);
    }

    /// <summary>POST с произвольным (в т.ч. некорректным) телом и заголовком Idempotency-Key.</summary>
    public static Task<HttpResponseMessage> PostRawWithKeyAsync(
        this HttpClient client, string url, string body, string? key = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
        };
        request.Headers.Add(KeyHeader, key ?? Guid.NewGuid().ToString());
        return client.SendAsync(request);
    }
}
