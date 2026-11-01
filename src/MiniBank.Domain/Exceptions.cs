namespace MiniBank.Domain;

public abstract class DomainException(string message) : Exception(message);

public sealed class DomainValidationException(string message) : DomainException(message);

public sealed class InsufficientFundsException(string message) : DomainException(message);

public sealed class AccountBlockedException(string message) : DomainException(message);

public sealed class PerOperationLimitExceededException(long limit)
    : DomainException($"The amount exceeds the per-operation limit of {limit}.")
{
    public long Limit { get; } = limit;
}

public sealed class DailyLimitExceededException(long limit, long remaining)
    : DomainException($"The amount exceeds the daily limit of {limit}; {remaining} remains for today.")
{
    public long Limit { get; } = limit;
    public long Remaining { get; } = remaining;
}
