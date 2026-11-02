using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MiniBank.Messaging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace MiniBank.Notifications;

/// <summary>
/// Читает очередь <c>minibank.notifications</c> с ручными подтверждениями. Сбой обработки повторяется не более
/// <see cref="MaxRetries"/> раз (счётчик в заголовке <c>x-attempt</c>), затем сообщение уходит в dead-letter очередь;
/// битое сообщение уходит туда сразу, чтобы не блокировать очередь.
/// </summary>
public sealed class EventConsumer(RabbitMqOptions options, EventHandler handler, ILogger<EventConsumer> logger)
    : BackgroundService
{
    public const int MaxRetries = 3;
    public const string AttemptHeader = "x-attempt";

    private const ushort Prefetch = 10;
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(500);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        IConnection? connection = null;
        IChannel? channel = null;
        try
        {
            while (channel is null)
            {
                try
                {
                    connection = await options.CreateConnectionFactory().CreateConnectionAsync(stoppingToken);
                    channel = await connection.CreateChannelAsync(cancellationToken: stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning("RabbitMQ is not available yet ({ErrorType}); retrying in {Delay}", ex.GetType().Name, ReconnectDelay);
                    await Task.Delay(ReconnectDelay, stoppingToken);
                }
            }

            await RabbitMqTopology.DeclareAsync(channel, stoppingToken);
            await channel.BasicQosAsync(0, Prefetch, global: false, stoppingToken);

            var consumer = new AsyncEventingBasicConsumer(channel);
            consumer.ReceivedAsync += (_, delivery) => OnReceivedAsync(channel, delivery, stoppingToken);
            await channel.BasicConsumeAsync(RabbitMqTopology.Queue, autoAck: false, consumer, stoppingToken);
            logger.LogInformation("Consuming {Queue}", RabbitMqTopology.Queue);

            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            if (channel is not null) await channel.DisposeAsync();
            if (connection is not null) await connection.DisposeAsync();
        }
    }

    private async Task OnReceivedAsync(IChannel channel, BasicDeliverEventArgs delivery, CancellationToken ct)
    {
        var messageId = delivery.BasicProperties.MessageId;
        try
        {
            var result = await handler.HandleAsync(delivery.Body, ct);
            logger.LogDebug("Message {MessageId} handled: {Result}", messageId, result);
            await channel.BasicAckAsync(delivery.DeliveryTag, multiple: false, ct);
        }
        catch (MalformedEventException ex)
        {
            logger.LogError("Message {MessageId} is malformed ({ErrorType}); sending to dead-letter", messageId, ex.GetType().Name);
            await channel.BasicNackAsync(delivery.DeliveryTag, multiple: false, requeue: false, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Остановка: без подтверждения брокер вернёт сообщение в очередь.
        }
        catch (Exception ex)
        {
            await RetryOrDeadLetterAsync(channel, delivery, ex, ct);
        }
    }

    private async Task RetryOrDeadLetterAsync(IChannel channel, BasicDeliverEventArgs delivery, Exception ex, CancellationToken ct)
    {
        var attempt = ReadAttempt(delivery.BasicProperties.Headers);
        var messageId = delivery.BasicProperties.MessageId;
        if (attempt >= MaxRetries)
        {
            logger.LogError("Message {MessageId} failed after {Attempt} retries ({ErrorType}); sending to dead-letter",
                messageId, attempt, ex.GetType().Name);
            await channel.BasicNackAsync(delivery.DeliveryTag, multiple: false, requeue: false, ct);
            return;
        }

        logger.LogWarning("Message {MessageId} failed ({ErrorType}); retry {Attempt} of {Max}",
            messageId, ex.GetType().Name, attempt + 1, MaxRetries);
        await Task.Delay(RetryDelay * (attempt + 1), ct);

        var headers = new Dictionary<string, object?>(delivery.BasicProperties.Headers ?? new Dictionary<string, object?>())
        {
            [AttemptHeader] = attempt + 1,
        };
        var properties = new BasicProperties
        {
            Persistent = true,
            MessageId = messageId,
            ContentType = delivery.BasicProperties.ContentType,
            Type = delivery.BasicProperties.Type,
            Headers = headers,
        };
        // Через пустой обменник прямо в очередь; исходное сообщение подтверждается только после повторной публикации.
        await channel.BasicPublishAsync(string.Empty, RabbitMqTopology.Queue, mandatory: false, properties, delivery.Body, ct);
        await channel.BasicAckAsync(delivery.DeliveryTag, multiple: false, ct);
    }

    private static int ReadAttempt(IDictionary<string, object?>? headers) =>
        headers is not null && headers.TryGetValue(AttemptHeader, out var value) && value is not null
            ? Convert.ToInt32(value)
            : 0;
}
