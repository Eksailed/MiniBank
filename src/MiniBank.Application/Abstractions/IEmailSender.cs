namespace MiniBank.Application.Abstractions;

public sealed record EmailMessage(string To, string Subject, string Body);

public interface IEmailSender
{
    Task SendAsync(EmailMessage message, CancellationToken ct);
}
