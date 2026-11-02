using Microsoft.Extensions.Configuration;
using RabbitMQ.Client;

namespace MiniBank.Messaging;

public sealed class RabbitMqOptions
{
    public string Host { get; init; } = "localhost";
    public int Port { get; init; } = 5672;
    public string User { get; init; } = "guest";
    public string Password { get; init; } = "guest";
    public string VirtualHost { get; init; } = "/";

    public static RabbitMqOptions Load(IConfiguration configuration)
    {
        var section = configuration.GetSection("RabbitMq");
        var options = new RabbitMqOptions
        {
            Host = section["Host"] ?? "localhost",
            Port = section.GetValue("Port", 5672),
            User = section["User"] ?? "guest",
            Password = section["Password"] ?? "guest",
            VirtualHost = section["VirtualHost"] ?? "/",
        };
        options.Validate();
        return options;
    }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Host))
            throw new InvalidOperationException("RabbitMq:Host is required.");
        if (Port is < 1 or > 65535)
            throw new InvalidOperationException("RabbitMq:Port must be between 1 and 65535.");
        if (string.IsNullOrWhiteSpace(User))
            throw new InvalidOperationException("RabbitMq:User is required.");
        if (string.IsNullOrWhiteSpace(VirtualHost))
            throw new InvalidOperationException("RabbitMq:VirtualHost is required.");
    }

    public ConnectionFactory CreateConnectionFactory() => new()
    {
        HostName = Host,
        Port = Port,
        UserName = User,
        Password = Password,
        VirtualHost = VirtualHost,
        AutomaticRecoveryEnabled = true,
        RequestedConnectionTimeout = TimeSpan.FromSeconds(5),
    };
}
