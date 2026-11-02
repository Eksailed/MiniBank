using System.Text.Json;
using MiniBank.Contracts;
using MiniBank.Infrastructure.Persistence;
using MiniBank.Messaging;
using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;

namespace MiniBank.Infrastructure.Events;

/// <summary>Публикует строки outbox в обменник <c>minibank.events</c> с подтверждением брокера (publisher confirms).</summary>
public sealed class RabbitMqOutboxSink(RabbitMqOptions options) : IOutboxSink, IAsyncDisposable
{
    private static readonly TimeSpan PublishTimeout = TimeSpan.FromSeconds(30);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IConnection? _connection;
    private IChannel? _channel;

    public async Task PublishAsync(OutboxMessage message, CancellationToken ct)
    {
        var body = BuildBody(message);
        var properties = new BasicProperties
        {
            Persistent = true,
            MessageId = message.Id.ToString(),
            ContentType = "application/json",
            Type = message.Type,
            Timestamp = new AmqpTimestamp(message.OccurredAt.ToUnixTimeSeconds()),
        };

        await _gate.WaitAsync(ct);
        try
        {
            // Без тайм-аута зависший confirm (например, при блокировке брокера по памяти) держал бы транзакцию с блокировками строк.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(PublishTimeout);
            var channel = await EnsureChannelAsync(timeout.Token);
            await channel.BasicPublishAsync(
                RabbitMqTopology.Exchange, message.Type, mandatory: false, basicProperties: properties, body: body, cancellationToken: timeout.Token);
        }
        catch (PublishException)
        {
            // Брокер ответил nack на это сообщение: ошибка строки, а не брокера.
            await ResetAsync();
            throw;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            await ResetAsync();
            throw new BrokerUnavailableException("Брокер недоступен или не подтвердил публикацию", ex);
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            await ResetAsync();
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static byte[] BuildBody(OutboxMessage message)
    {
        using var data = JsonDocument.Parse(message.Payload);
        var envelope = new EventEnvelope(message.Id, message.Type, message.Version, message.OccurredAt, data.RootElement);
        return JsonSerializer.SerializeToUtf8Bytes(envelope, EventJson.Options);
    }

    private async Task<IChannel> EnsureChannelAsync(CancellationToken ct)
    {
        if (_channel is { IsOpen: true })
            return _channel;

        await ResetAsync();
        _connection = await options.CreateConnectionFactory().CreateConnectionAsync(ct);
        _channel = await _connection.CreateChannelAsync(
            new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true),
            ct);
        await RabbitMqTopology.DeclareAsync(_channel, ct);
        return _channel;
    }

    private async Task ResetAsync()
    {
        var channel = _channel;
        var connection = _connection;
        _channel = null;
        _connection = null;
        try
        {
            if (channel is not null) await channel.DisposeAsync();
            if (connection is not null) await connection.DisposeAsync();
        }
        catch
        {
            // Соединение уже сломано: освобождать нечего.
        }
    }

    public async ValueTask DisposeAsync()
    {
        await ResetAsync();
        _gate.Dispose();
    }
}
