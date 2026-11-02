namespace MiniBank.Application.Abstractions;

/// <summary>Порт к сервису уведомлений. Любой сбой связи или настройки — <see cref="NotificationsUnavailableException"/>.</summary>
public interface INotificationsGateway
{
    Task<TelegramStatusDto> GetAsync(Guid userId, CancellationToken ct);

    Task<LinkDto> CreateLinkAsync(Guid userId, CancellationToken ct);

    Task UnlinkAsync(Guid userId, CancellationToken ct);

    Task UpdateSettingsAsync(Guid userId, NotificationSettingsDto settings, CancellationToken ct);
}
