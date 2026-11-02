using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MiniBank.Messaging;

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

    public static async Task MigrateNotificationsAsync(this IServiceProvider services, CancellationToken ct = default)
    {
        var factory = services.GetRequiredService<IDbContextFactory<NotificationsDbContext>>();
        await using var db = await factory.CreateDbContextAsync(ct);
        await db.Database.MigrateAsync(ct);
    }
}
