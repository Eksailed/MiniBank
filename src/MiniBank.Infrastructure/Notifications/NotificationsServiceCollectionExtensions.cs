using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MiniBank.Application;
using MiniBank.Application.Abstractions;

namespace MiniBank.Infrastructure.Notifications;

public static class NotificationsServiceCollectionExtensions
{
    /// <summary>
    /// Шлюз к сервису уведомлений. Без <c>Notifications:BaseUrl</c> регистрируется заглушка, отвечающая
    /// <see cref="NotificationsUnavailableException"/>, и остальной API работает как обычно.
    /// </summary>
    public static IServiceCollection AddNotificationsGateway(this IServiceCollection services, IConfiguration configuration)
    {
        var options = NotificationsGatewayOptions.Load(configuration);
        services.AddSingleton(options);
        if (!options.Enabled)
        {
            services.AddSingleton<INotificationsGateway, UnavailableNotificationsGateway>();
            return services;
        }

        services.AddHttpClient<INotificationsGateway, NotificationsHttpGateway>(client =>
        {
            client.BaseAddress = new Uri(options.BaseUrl);
            client.Timeout = options.Timeout;
        });
        return services;
    }

    private sealed class UnavailableNotificationsGateway : INotificationsGateway
    {
        public Task<TelegramStatusDto> GetAsync(Guid userId, CancellationToken ct) => throw new NotificationsUnavailableException();

        public Task<LinkDto> CreateLinkAsync(Guid userId, CancellationToken ct) => throw new NotificationsUnavailableException();

        public Task UnlinkAsync(Guid userId, CancellationToken ct) => throw new NotificationsUnavailableException();

        public Task UpdateSettingsAsync(Guid userId, NotificationSettingsDto settings, CancellationToken ct) =>
            throw new NotificationsUnavailableException();
    }
}
