using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MiniBank.Infrastructure.Events;

/// <summary>Фоновый воркер: забирает пачки из outbox и отправляет их в брокер; недоступность брокера API не ломает.</summary>
public sealed class OutboxPublisher(
    IServiceScopeFactory scopes, IOutboxSink sink, EventsOptions options, TimeProvider clock, ILogger<OutboxPublisher> logger)
    : BackgroundService
{
    private static readonly TimeSpan MaintenanceInterval = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var nextMaintenance = DateTimeOffset.MinValue;
        while (!stoppingToken.IsCancellationRequested)
        {
            var processed = 0;
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var processor = scope.ServiceProvider.GetRequiredService<OutboxBatchProcessor>();
                processed = await processor.ProcessBatchAsync(sink, stoppingToken);

                if (clock.GetUtcNow() >= nextMaintenance)
                {
                    nextMaintenance = clock.GetUtcNow() + MaintenanceInterval;
                    await processor.CleanupAsync(stoppingToken);
                    await WarnIfStaleAsync(processor, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError("Outbox publisher iteration failed: {ErrorType}", ex.GetType().Name);
            }

            // Полная пачка: есть что забирать дальше; иначе (или после сбоя) ждём.
            if (processed < options.BatchSize)
                await DelayAsync(stoppingToken);
        }
    }

    private async Task WarnIfStaleAsync(OutboxBatchProcessor processor, CancellationToken ct)
    {
        var age = await processor.OldestPendingAgeAsync(ct);
        if (age > options.StaleAfter)
            logger.LogWarning("The oldest unsent outbox event is {Age} old (threshold {Threshold})", age, options.StaleAfter);
    }

    private async Task DelayAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(options.PollInterval, clock, ct);
        }
        catch (OperationCanceledException)
        {
        }
    }
}
