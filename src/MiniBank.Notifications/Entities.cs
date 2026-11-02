namespace MiniBank.Notifications;

/// <summary>Привязка пользователя к чату Telegram; и пользователь, и чат встречаются не больше одного раза.</summary>
public class TelegramLink
{
    public Guid UserId { get; set; }
    public long ChatId { get; set; }
    public DateTimeOffset LinkedAt { get; set; }
}

/// <summary>Одноразовый код привязки; хранится только SHA-256 кода.</summary>
public class LinkCode
{
    public string CodeHash { get; set; } = "";
    public Guid UserId { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
}

/// <summary>Настройки уведомлений; отсутствие строки означает значения по умолчанию (всё включено).</summary>
public class NotificationSettings
{
    public Guid UserId { get; set; }
    public bool Enabled { get; set; } = true;
    public bool OnTransferSent { get; set; } = true;
    public bool OnTransferReceived { get; set; } = true;
    public bool OnDeposit { get; set; } = true;
}

public enum DeliveryStatus { Pending, Sent, Failed }

/// <summary>Сообщение в очереди на отправку в Telegram; по одной на запись <c>received_events</c>.</summary>
public class TelegramDelivery
{
    public Guid Id { get; set; }
    public Guid ReceivedEventId { get; set; }
    public long ChatId { get; set; }
    public string Text { get; set; } = "";
    public DeliveryStatus Status { get; set; } = DeliveryStatus.Pending;
    public int Attempts { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; }
    public DateTimeOffset? SentAt { get; set; }
    public string? LastError { get; set; }
}
