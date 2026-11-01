using System.Text.RegularExpressions;
using MiniBank.Application.Abstractions;

namespace MiniBank.TestSupport;

/// <summary>Отправитель писем для тестов: запоминает письма, может имитировать сбой SMTP.</summary>
public sealed partial class FakeEmailSender : IEmailSender
{
    private readonly object _gate = new();
    private readonly List<EmailMessage> _sent = [];
    private readonly List<EmailMessage> _attempted = [];

    /// <summary>Если задано, <see cref="SendAsync"/> бросает это исключение (после записи попытки).</summary>
    public Exception? FailWith { get; set; }

    /// <summary>Успешно отправленные письма.</summary>
    public IReadOnlyList<EmailMessage> Sent
    {
        get { lock (_gate) return [.. _sent]; }
    }

    /// <summary>Все попытки отправки, включая неудачные.</summary>
    public IReadOnlyList<EmailMessage> Attempted
    {
        get { lock (_gate) return [.. _attempted]; }
    }

    public Task SendAsync(EmailMessage message, CancellationToken ct)
    {
        lock (_gate)
        {
            _attempted.Add(message);
            if (FailWith is null)
                _sent.Add(message);
        }

        return FailWith is { } failure ? Task.FromException(failure) : Task.CompletedTask;
    }

    /// <summary>Токен из ссылки в последнем письме на этот адрес.</summary>
    public string LastConfirmationToken(string email)
    {
        var message = Attempted.LastOrDefault(m => string.Equals(m.To, email, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"No email was sent to {email}.");
        var match = TokenPattern().Match(message.Body);
        return match.Success
            ? match.Groups[1].Value
            : throw new InvalidOperationException("The email has no confirmation link.");
    }

    [GeneratedRegex(@"confirm-email#token=([A-Za-z0-9_-]+)")]
    private static partial Regex TokenPattern();
}
