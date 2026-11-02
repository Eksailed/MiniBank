using RabbitMQ.Client;

namespace MiniBank.Messaging;

/// <summary>Топология брокера. Объявляется идемпотентно и издателем, и потребителем.</summary>
public static class RabbitMqTopology
{
    public const string Exchange = "minibank.events";
    public const string DeadExchange = "minibank.events.dead";
    public const string Queue = "minibank.notifications";
    public const string DeadQueue = "minibank.notifications.dead";

    public static async Task DeclareAsync(IChannel channel, CancellationToken ct)
    {
        await channel.ExchangeDeclareAsync(Exchange, ExchangeType.Topic, durable: true, cancellationToken: ct);
        await channel.ExchangeDeclareAsync(DeadExchange, ExchangeType.Fanout, durable: true, cancellationToken: ct);

        await channel.QueueDeclareAsync(DeadQueue, durable: true, exclusive: false, autoDelete: false, cancellationToken: ct);
        await channel.QueueBindAsync(DeadQueue, DeadExchange, routingKey: string.Empty, cancellationToken: ct);

        await channel.QueueDeclareAsync(Queue, durable: true, exclusive: false, autoDelete: false,
            arguments: new Dictionary<string, object?> { ["x-dead-letter-exchange"] = DeadExchange }, cancellationToken: ct);
        await channel.QueueBindAsync(Queue, Exchange, routingKey: "#", cancellationToken: ct);
    }
}
