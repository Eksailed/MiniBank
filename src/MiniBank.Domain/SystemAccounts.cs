namespace MiniBank.Domain;

/// <summary>Системные счета «Касса»: по одному на каждую поддерживаемую валюту, создаются миграцией.</summary>
public static class SystemAccounts
{
    private static readonly IReadOnlyDictionary<string, Guid> CashIds = new Dictionary<string, Guid>
    {
        ["RUB"] = Guid.Parse("00000000-0000-0000-0000-0000000000a1"),
        ["USD"] = Guid.Parse("00000000-0000-0000-0000-0000000000a2"),
        ["EUR"] = Guid.Parse("00000000-0000-0000-0000-0000000000a3")
    };

    public static Guid CashId(string currency) => CashIds[Currencies.Normalize(currency)];

    public static bool IsCashId(Guid id) => CashIds.Values.Contains(id);
}
