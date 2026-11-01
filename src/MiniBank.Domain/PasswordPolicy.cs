namespace MiniBank.Domain;

public static class PasswordPolicy
{
    public const int MinLength = 8;
    public const int MaxLength = 128;

    public static void Validate(string? password)
    {
        if (password is null || password.Length < MinLength)
            throw new DomainValidationException($"Password must be at least {MinLength} characters.");
        if (password.Length > MaxLength)
            throw new DomainValidationException($"Password must be at most {MaxLength} characters.");
    }
}
