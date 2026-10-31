namespace MiniBank.Domain;

/// <summary>Положительная сумма в минимальных единицах валюты (копейках).</summary>
public readonly record struct Money(long Amount, string Currency)
{
    public static Money Of(long amount, string? currency)
    {
        if (amount <= 0)
            throw new DomainValidationException("Amount must be greater than zero.");
        return new Money(amount, Currencies.Normalize(currency));
    }
}
