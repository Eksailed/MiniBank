using System.Text.Json;

namespace MiniBank.Contracts;

public static class EventTypes
{
    public const string TransferCompleted = "transfer.completed";
    public const string AccountDeposited = "account.deposited";
}

/// <summary>Сериализация событий: camelCase, как в конверте.</summary>
public static class EventJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
}

public abstract record IntegrationEvent
{
    public abstract string Type { get; }

    public virtual int Version => 1;
}

public sealed record TransferCompleted(
    Guid TransferId, Guid FromAccountId, Guid ToAccountId, Guid FromOwnerId, Guid? ToOwnerId, long Amount, string Currency)
    : IntegrationEvent
{
    public override string Type => EventTypes.TransferCompleted;
}

public sealed record AccountDeposited(Guid TransferId, Guid AccountId, Guid OwnerId, long Amount, string Currency)
    : IntegrationEvent
{
    public override string Type => EventTypes.AccountDeposited;
}

/// <summary>Конверт сообщения в брокере; <c>EventId</c> совпадает с id строки outbox.</summary>
public sealed record EventEnvelope(Guid EventId, string Type, int Version, DateTimeOffset OccurredAt, JsonElement Data);
