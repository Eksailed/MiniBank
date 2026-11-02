using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MiniBank.Messaging;
using MiniBank.Notifications.Linking;
using MiniBank.Notifications.Telegram;

namespace MiniBank.Notifications;

public static class NotificationsServiceCollectionExtensions
{
    /// <summary>Настройки <c>ConnectionStrings:Notifications</c> и <c>RabbitMq:*</c> проверяются при старте.</summary>
    public static IServiceCollection AddNotifications(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Notifications");
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException("ConnectionStrings:Notifications is required.");

        services.AddDbContextFactory<NotificationsDbContext>(o => NotificationsDbContext.Configure(o, connectionString));
        services.AddSingleton(RabbitMqOptions.Load(configuration));
        services.AddSingleton<EventHandler>();
        services.AddHostedService<EventConsumer>();
        return services;
    }

    /// <summary>Бот, отправитель доставок и внутренний API; токен, имя бота и секрет обязательны.</summary>
    public static IServiceCollection AddTelegram(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton(TelegramOptions.Load(configuration));
        services.AddSingleton(TimeProvider.System);
        services.AddHttpClient<ITelegramClient, TelegramClient>(c => c.Timeout = Timeout.InfiniteTimeSpan)
            // Токен бота стоит в пути запроса, а встроенное логирование HttpClientFactory пишет URL целиком.
            .RemoveAllLoggers();
        services.AddSingleton<DeliveryBatchProcessor>();
        services.AddHostedService<DeliverySender>();
        services.AddSingleton<LinkService>();
        services.AddHostedService<BotPoller>();
        return services;
    }

    public static async Task MigrateNotificationsAsync(this IServiceProvider services, CancellationToken ct = default)
    {
        var factory = services.GetRequiredService<IDbContextFactory<NotificationsDbContext>>();
        await using var db = await factory.CreateDbContextAsync(ct);
        await db.Database.MigrateAsync(ct);
    }
}
