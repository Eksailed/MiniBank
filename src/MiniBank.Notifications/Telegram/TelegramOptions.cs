using Microsoft.Extensions.Configuration;

namespace MiniBank.Notifications.Telegram;

/// <summary>Настройки бота, доставки и внутреннего API. Токен и секрет обязательны, без них сервис не стартует.</summary>
public sealed class TelegramOptions
{
    public const int MinSecretLength = 16;

    public string BotToken { get; init; } = "";
    public string BotUsername { get; init; } = "";
    public string ApiBaseUrl { get; init; } = "https://api.telegram.org";
    public string InternalSecret { get; init; } = "";
    public TimeSpan DeliveryPollInterval { get; init; } = TimeSpan.FromSeconds(1);
    public int DeliveryBatchSize { get; init; } = 20;
    public int DeliveryMaxAttempts { get; init; } = 10;
    public int RetentionDays { get; init; } = 30;

    public static TelegramOptions Load(IConfiguration configuration)
    {
        var telegram = configuration.GetSection("Telegram");
        var notifications = configuration.GetSection("Notifications");
        var defaults = new TelegramOptions();

        var options = new TelegramOptions
        {
            BotToken = telegram["BotToken"] ?? "",
            BotUsername = (telegram["BotUsername"] ?? "").TrimStart('@'),
            ApiBaseUrl = (telegram["ApiBaseUrl"] ?? defaults.ApiBaseUrl).TrimEnd('/'),
            InternalSecret = notifications["InternalSecret"] ?? "",
            DeliveryPollInterval = notifications.GetValue("DeliveryPollInterval", defaults.DeliveryPollInterval),
            DeliveryBatchSize = notifications.GetValue("DeliveryBatchSize", defaults.DeliveryBatchSize),
            DeliveryMaxAttempts = notifications.GetValue("DeliveryMaxAttempts", defaults.DeliveryMaxAttempts),
            RetentionDays = notifications.GetValue("RetentionDays", defaults.RetentionDays),
        };

        if (string.IsNullOrWhiteSpace(options.BotToken))
            throw new InvalidOperationException("Telegram:BotToken is required.");
        if (string.IsNullOrWhiteSpace(options.BotUsername))
            throw new InvalidOperationException("Telegram:BotUsername is required.");
        if (options.InternalSecret.Length < MinSecretLength)
            throw new InvalidOperationException($"Notifications:InternalSecret must be at least {MinSecretLength} characters.");
        if (options.DeliveryPollInterval <= TimeSpan.Zero || options.DeliveryBatchSize < 1
            || options.DeliveryMaxAttempts < 1 || options.RetentionDays < 1)
            throw new InvalidOperationException("Notifications delivery settings must be positive.");
        return options;
    }
}
