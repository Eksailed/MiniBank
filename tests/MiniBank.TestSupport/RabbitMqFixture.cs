using System.Text;
using MiniBank.Messaging;
using RabbitMQ.Client;
using Testcontainers.RabbitMq;
using Xunit;

namespace MiniBank.TestSupport;

public sealed record ReceivedMessage(string? MessageId, string RoutingKey, string Body, IDictionary<string, object?>? Headers);

/// <summary>Контейнер RabbitMQ; тест, которому нужен чистый брокер, зовёт <see cref="ResetAsync"/>.</summary>
public sealed class RabbitMqFixture : IAsyncLifetime
{
    private readonly RabbitMqContainer _container = new RabbitMqBuilder("rabbitmq:4-management").Build();

    public RabbitMqOptions Options => new()
    {
        Host = _container.Hostname,
        Port = _container.GetMappedPublicPort(5672),
        User = RabbitMqBuilder.DefaultUsername,
        Password = RabbitMqBuilder.DefaultPassword,
    };

    /// <summary>Адрес без брокера: порт закрыт, подключение сразу отклоняется.</summary>
    public static RabbitMqOptions Unreachable => new() { Host = "127.0.0.1", Port = 1 };

    public Task InitializeAsync() => _container.StartAsync();

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    public async Task<IConnection> ConnectAsync() => await Options.CreateConnectionFactory().CreateConnectionAsync();

    /// <summary>Объявляет топологию и очищает обе очереди.</summary>
    public async Task ResetAsync()
    {
        await using var connection = await ConnectAsync();
        await using var channel = await connection.CreateChannelAsync();
        await RabbitMqTopology.DeclareAsync(channel, default);
        await channel.QueuePurgeAsync(RabbitMqTopology.Queue);
        await channel.QueuePurgeAsync(RabbitMqTopology.DeadQueue);
    }

    public async Task<uint> MessageCountAsync(string queue)
    {
        await using var connection = await ConnectAsync();
        await using var channel = await connection.CreateChannelAsync();
        return (await channel.QueueDeclarePassiveAsync(queue)).MessageCount;
    }

    /// <summary>Забирает (с подтверждением) до <paramref name="expected"/> сообщений, ожидая не дольше <paramref name="timeout"/>.</summary>
    public async Task<List<ReceivedMessage>> TakeAsync(string queue, int expected, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(15));
        var messages = new List<ReceivedMessage>();
        await using var connection = await ConnectAsync();
        await using var channel = await connection.CreateChannelAsync();
        while (messages.Count < expected && DateTime.UtcNow < deadline)
        {
            var result = await channel.BasicGetAsync(queue, autoAck: true);
            if (result is null)
            {
                await Task.Delay(100);
                continue;
            }

            messages.Add(new ReceivedMessage(
                result.BasicProperties.MessageId, result.RoutingKey, Encoding.UTF8.GetString(result.Body.Span), result.BasicProperties.Headers));
        }

        return messages;
    }
}
