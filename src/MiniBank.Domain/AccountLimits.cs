namespace MiniBank.Domain;

/// <summary>Лимиты исходящих переводов счёта в минорных единицах валюты счёта.</summary>
public sealed record AccountLimits
{
    public AccountLimits(long perOperation, long daily)
    {
        if (perOperation <= 0)
            throw new DomainValidationException("Per-operation limit must be positive.");
        if (daily <= 0)
            throw new DomainValidationException("Daily limit must be positive.");
        if (daily < perOperation)
            throw new DomainValidationException("Daily limit must not be less than the per-operation limit.");

        PerOperation = perOperation;
        Daily = daily;
    }

    public long PerOperation { get; }
    public long Daily { get; }
}
