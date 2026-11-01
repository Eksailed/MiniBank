using System.Text;

namespace MiniBank.Api.Auth;

public sealed class JwtOptions
{
    public const int MinKeyBytes = 32;

    public string Key { get; init; } = string.Empty;

    public string Issuer { get; init; } = "minibank";

    public string Audience { get; init; } = "minibank-web";

    public int LifetimeMinutes { get; init; } = 60;

    public byte[] GetKeyBytes() => Encoding.UTF8.GetBytes(Key);

    /// <summary>Читает раздел <c>Jwt</c> и проверяет его: без надёжного ключа API не должен стартовать.</summary>
    public static JwtOptions Load(IConfiguration configuration)
    {
        var options = configuration.GetSection("Jwt").Get<JwtOptions>() ?? new JwtOptions();

        if (Encoding.UTF8.GetByteCount(options.Key) < MinKeyBytes)
            throw new InvalidOperationException($"Jwt:Key is required and must be at least {MinKeyBytes} bytes long.");
        if (string.IsNullOrWhiteSpace(options.Issuer) || string.IsNullOrWhiteSpace(options.Audience))
            throw new InvalidOperationException("Jwt:Issuer and Jwt:Audience must not be empty.");
        if (options.LifetimeMinutes <= 0)
            throw new InvalidOperationException("Jwt:LifetimeMinutes must be positive.");

        return options;
    }
}
