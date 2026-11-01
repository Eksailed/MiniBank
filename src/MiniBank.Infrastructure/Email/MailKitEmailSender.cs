using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using MiniBank.Application.Abstractions;

namespace MiniBank.Infrastructure.Email;

internal sealed class MailKitEmailSender(SmtpOptions options) : IEmailSender
{
    public async Task SendAsync(EmailMessage message, CancellationToken ct)
    {
        var mime = new MimeMessage();
        mime.From.Add(MailboxAddress.Parse(options.From));
        mime.To.Add(MailboxAddress.Parse(message.To));
        mime.Subject = message.Subject;
        mime.Body = new TextPart("plain") { Text = message.Body };

        using var client = new SmtpClient();
        var security = options.UseStartTls ? SecureSocketOptions.StartTls : SecureSocketOptions.None;
        await client.ConnectAsync(options.Host, options.Port, security, ct);
        if (!string.IsNullOrEmpty(options.Username))
            await client.AuthenticateAsync(options.Username, options.Password ?? string.Empty, ct);
        await client.SendAsync(mime, ct);
        await client.DisconnectAsync(true, ct);
    }
}
