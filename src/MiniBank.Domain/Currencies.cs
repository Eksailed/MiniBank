namespace MiniBank.Domain;

public static class Currencies
{
    public static readonly IReadOnlyList<string> Supported = ["RUB", "USD", "EUR"];

    public static string Normalize(string? code)
    {
        var normalized = code?.Trim().ToUpperInvariant();
        if (normalized is null || !Supported.Contains(normalized))
            throw new DomainValidationException($"Unsupported currency '{code}'.");
        return normalized;
    }
}
