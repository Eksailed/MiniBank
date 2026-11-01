namespace MiniBank.Domain;

public static class EmailAddress
{
    public const int MaxLength = 254;

    public static string Normalize(string? email)
    {
        var value = email?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(value))
            throw new DomainValidationException("Email must not be empty.");
        if (value.Length > MaxLength)
            throw new DomainValidationException($"Email must be at most {MaxLength} characters.");
        if (value.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
            throw new DomainValidationException("Email is not valid.");

        var at = value.IndexOf('@');
        if (at <= 0 || at != value.LastIndexOf('@'))
            throw new DomainValidationException("Email is not valid.");

        var domain = value[(at + 1)..];
        var dot = domain.IndexOf('.');
        if (dot <= 0 || domain.StartsWith('.') || domain.EndsWith('.') || domain.Contains(".."))
            throw new DomainValidationException("Email is not valid.");

        return value;
    }
}
