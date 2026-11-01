using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace MiniBank.Application.Auth;

public static class ConfirmationTokens
{
    private const int TokenBytes = 32;

    /// <summary>Новый случайный токен: <c>Raw</c> уходит в письмо, в БД хранится только <c>Hash</c>.</summary>
    public static (string Raw, string Hash) Generate()
    {
        var raw = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(TokenBytes));
        return (raw, Hash(raw));
    }

    public static string Hash(string raw) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
}
