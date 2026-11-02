using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MiniBank.Notifications.Telegram;

/// <summary>Фоновый цикл: отправляет ожидающие доставки и раз в минуту чистит старые.</summary>
public sealed class DeliverySender(
    DeliveryBatchProcessor processor, TelegramOptions options, TimeProvider clock, ILogger<DeliverySender> logger) : BackgroundService
{
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan ErrorPause = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var nextCleanup = clock.GetUtcNow();
        while (!stoppingToken.IsCancellationRequested)
        {
            var pause = options.DeliveryPollInterval;
            try
            {
                var processed = await processor.ProcessBatchAsync(stoppingToken);
                if (processed >= options.DeliveryBatchSize)
                    pause = TimeSpan.Zero;

                if (clock.GetUtcNow() >= nextCleanup)
                {
                    await processor.CleanupAsync(stoppingToken);
                    nextCleanup = clock.GetUtcNow() + CleanupInterval;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError("Delivery pass failed: {ErrorType}", ex.GetType().Name);
                pause = ErrorPause;
            }

            try
            {
                await Task.Delay(pause, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
