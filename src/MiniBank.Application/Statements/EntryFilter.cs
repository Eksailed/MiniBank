using MiniBank.Domain;

namespace MiniBank.Application.Statements;

public enum EntryDirection
{
    /// <summary>Положительные проводки: пополнения и входящие переводы.</summary>
    In,

    /// <summary>Отрицательные проводки: исходящие переводы.</summary>
    Out
}

/// <summary>Фильтр выписки: <c>From</c> включительно, <c>To</c> не включительно, оба в UTC.</summary>
public sealed record EntryFilter
{
    public EntryFilter(DateTimeOffset? from, DateTimeOffset? to, EntryDirection? direction)
    {
        if (from is { } start && to is { } end && start >= end)
            throw new DomainValidationException("'from' must be earlier than 'to'.");

        From = from;
        To = to;
        Direction = direction;
    }

    public static EntryFilter None { get; } = new(null, null, null);

    public DateTimeOffset? From { get; }
    public DateTimeOffset? To { get; }
    public EntryDirection? Direction { get; }
}
