using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MiniBank.Notifications.Linking;

namespace MiniBank.Notifications.Telegram;

/// <summary>Long polling бота: принимает <c>/start &lt;код&gt;</c> и привязывает чат.</summary>
public sealed class BotPoller(ITelegramClient telegram, LinkService links, ILogger<BotPoller> logger) : BackgroundService
{
    public const string LinkedReply = "Готово. Уведомления включены.";
    public const string InvalidCodeReply = "Код недействителен или устарел.";
    public const string HelpReply = "Привязать Telegram можно в MiniBank: откройте «Профиль → Уведомления в Telegram» и нажмите «Привязать».";

    private static readonly TimeSpan PollTimeout = TimeSpan.FromSeconds(25);

    public TimeSpan ErrorPause { get; init; } = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        long offset = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                offset = await PollOnceAsync(offset, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning("Bot polling failed: {ErrorType}", ex.GetType().Name);
                try
                {
                    await Task.Delay(ErrorPause, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    /// <summary>Один запрос getUpdates и обработка полученного; возвращает следующий offset.</summary>
    public async Task<long> PollOnceAsync(long offset, CancellationToken ct)
    {
        var updates = await telegram.GetUpdatesAsync(offset, PollTimeout, ct);
        foreach (var update in updates)
        {
            // offset сдвигается и при сбое обработки: «ядовитое» сообщение не должно зациклить бота.
            offset = update.UpdateId + 1;
            if (update.ChatId is not { } chatId || update.Text is null)
                continue;
            try
            {
                await HandleAsync(chatId, update.Text, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning("Update handling failed: {ErrorType}", ex.GetType().Name);
            }
        }
        return offset;
    }

    private async Task HandleAsync(long chatId, string text, CancellationToken ct)
    {
        var parts = text.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var isStart = parts.Length > 0 && (parts[0] == "/start" || parts[0].StartsWith("/start@", StringComparison.Ordinal));
        if (isStart && parts.Length == 2)
        {
            var linked = await links.RedeemAsync(parts[1], chatId, ct);
            await telegram.SendMessageAsync(chatId, linked ? LinkedReply : InvalidCodeReply, ct);
            return;
        }
        await telegram.SendMessageAsync(chatId, HelpReply, ct);
    }
}
