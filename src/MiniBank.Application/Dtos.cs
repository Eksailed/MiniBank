namespace MiniBank.Application;

public sealed record AccountDto(
    Guid Id, string Owner, string Currency, long Balance, string Status, bool IsSystem, DateTimeOffset CreatedAt);

public sealed record LimitsDto(
    long PerOperation, long Daily, long SpentToday, long RemainingToday, long MaxPerOperation, long MaxDaily);

public sealed record EntryDto(Guid Id, Guid TransferId, long Amount, long BalanceAfter, DateTimeOffset CreatedAt);

public sealed record TransferDto(
    Guid Id, Guid FromAccountId, Guid ToAccountId, long Amount, string Currency, DateTimeOffset CreatedAt);

public sealed record NotificationSettingsDto(bool Enabled, bool OnTransferSent, bool OnTransferReceived, bool OnDeposit);

public sealed record TelegramStatusDto(bool Linked, DateTimeOffset? LinkedAt, NotificationSettingsDto Settings);

public sealed record LinkDto(string Url, DateTimeOffset ExpiresAt);
