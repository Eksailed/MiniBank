namespace MiniBank.Application.Limits;

public sealed class LimitPair
{
    public long PerOperation { get; init; }
    public long Daily { get; init; }
}

/// <summary>Умолчания и потолки лимитов по валютам (секция <c>Limits</c> конфигурации).</summary>
public sealed class LimitsOptions
{
    /// <summary>Лимиты нового счёта.</summary>
    public Dictionary<string, LimitPair> Default { get; init; } = [];

    /// <summary>Выше этих значений клиент не может поднять свои лимиты.</summary>
    public Dictionary<string, LimitPair> Max { get; init; } = [];

    /// <summary>Значения из <c>appsettings.json</c>; нужны, когда конфигурации нет (тесты без API).</summary>
    public static LimitsOptions Builtin => new()
    {
        Default = new()
        {
            ["RUB"] = new() { PerOperation = 10_000_000, Daily = 30_000_000 },
            ["USD"] = new() { PerOperation = 100_000, Daily = 300_000 },
            ["EUR"] = new() { PerOperation = 100_000, Daily = 300_000 }
        },
        Max = new()
        {
            ["RUB"] = new() { PerOperation = 100_000_000, Daily = 300_000_000 },
            ["USD"] = new() { PerOperation = 1_000_000, Daily = 3_000_000 },
            ["EUR"] = new() { PerOperation = 1_000_000, Daily = 3_000_000 }
        }
    };
}
