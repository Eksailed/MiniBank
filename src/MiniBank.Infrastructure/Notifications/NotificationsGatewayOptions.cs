using Microsoft.Extensions.Configuration;

namespace MiniBank.Infrastructure.Notifications;

/// <summary>Адрес и секрет внутреннего API сервиса уведомлений. Без <see cref="BaseUrl"/> уведомления отключены.</summary>
public sealed class NotificationsGatewayOptions
{
    public const int MinSecretLength = 16;

    public string BaseUrl { get; init; } = "";
    public string InternalSecret { get; init; } = "";
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(5);

    public bool Enabled => BaseUrl.Length > 0;

    public static NotificationsGatewayOptions Load(IConfiguration configuration)
    {
        var section = configuration.GetSection("Notifications");
        var options = new NotificationsGatewayOptions
        {
            BaseUrl = (section["BaseUrl"] ?? "").Trim(),
            InternalSecret = section["InternalSecret"] ?? "",
        };
        if (!options.Enabled)
            return options;

        if (!Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new InvalidOperationException("Notifications:BaseUrl must be an absolute http(s) URL.");
        if (options.InternalSecret.Length < MinSecretLength)
            throw new InvalidOperationException($"Notifications:InternalSecret must be at least {MinSecretLength} characters.");
        return options;
    }
}
