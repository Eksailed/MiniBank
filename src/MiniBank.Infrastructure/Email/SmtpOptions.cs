namespace MiniBank.Infrastructure.Email;

public sealed class SmtpOptions
{
    public string Host { get; init; } = "localhost";

    public int Port { get; init; } = 1025;

    public string From { get; init; } = "MiniBank <no-reply@minibank.local>";

    public bool UseStartTls { get; init; }

    public string? Username { get; init; }

    public string? Password { get; init; }
}
