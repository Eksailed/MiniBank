using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MiniBank.Messaging;

namespace MiniBank.Infrastructure.Events;

public static class EventsServiceCollectionExtensions
{
    /// <summary>
    /// Настройки <c>Events:*</c> и <c>RabbitMq:*</c> проверяются при старте. При <c>Events:Enabled=false</c> воркер
    /// не запускается, но события по-прежнему пишутся в outbox.
    /// </summary>
    public static IServiceCollection AddOutboxPublisher(this IServiceCollection services, IConfiguration configuration)
    {
        var events = EventsOptions.Load(configuration);
        services.AddSingleton(events);
        services.AddScoped<OutboxBatchProcessor>();
        if (!events.Enabled)
            return services;

        services.AddSingleton(RabbitMqOptions.Load(configuration));
        services.AddSingleton<IOutboxSink, RabbitMqOutboxSink>();
        services.AddHostedService<OutboxPublisher>();
        return services;
    }
}
