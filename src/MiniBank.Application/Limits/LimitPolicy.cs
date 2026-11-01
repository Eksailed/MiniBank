using MiniBank.Domain;

namespace MiniBank.Application.Limits;

/// <summary>Умолчания и потолки лимитов; конфигурацию проверяет при создании, чтобы ошибка всплыла при старте.</summary>
public sealed class LimitPolicy
{
    private readonly Dictionary<string, AccountLimits> _defaults = [];
    private readonly Dictionary<string, AccountLimits> _max = [];

    public LimitPolicy(LimitsOptions options)
    {
        foreach (var currency in Currencies.Supported)
        {
            var defaults = Read(options.Default, "Default", currency);
            var max = Read(options.Max, "Max", currency);
            if (defaults.PerOperation > max.PerOperation || defaults.Daily > max.Daily)
                throw new InvalidOperationException($"Limits:Default:{currency} must not exceed Limits:Max:{currency}.");

            _defaults[currency] = defaults;
            _max[currency] = max;
        }
    }

    /// <summary>Начало суток UTC, в которые попадает момент <paramref name="now"/>: с него считается дневной расход.</summary>
    public static DateTimeOffset StartOfDay(DateTimeOffset now) => new(now.UtcDateTime.Date, TimeSpan.Zero);

    public AccountLimits Default(string currency) => _defaults[Currencies.Normalize(currency)];

    public AccountLimits Max(string currency) => _max[Currencies.Normalize(currency)];

    public void EnsureWithinMax(string currency, AccountLimits limits)
    {
        var max = Max(currency);
        if (limits.PerOperation > max.PerOperation)
            throw new DomainValidationException($"Per-operation limit must not exceed {max.PerOperation}.");
        if (limits.Daily > max.Daily)
            throw new DomainValidationException($"Daily limit must not exceed {max.Daily}.");
    }

    private static AccountLimits Read(Dictionary<string, LimitPair> section, string name, string currency)
    {
        if (!section.TryGetValue(currency, out var pair))
            throw new InvalidOperationException($"Limits:{name}:{currency} is not configured.");

        try
        {
            return new AccountLimits(pair.PerOperation, pair.Daily);
        }
        catch (DomainValidationException ex)
        {
            throw new InvalidOperationException($"Limits:{name}:{currency} is invalid: {ex.Message}", ex);
        }
    }
}
