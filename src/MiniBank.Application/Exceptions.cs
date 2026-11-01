namespace MiniBank.Application;

public sealed class AccountNotFoundException(Guid id) : Exception($"Account {id} was not found.")
{
    public Guid AccountId { get; } = id;
}

public sealed class TransferNotAllowedException(string message) : Exception(message);

public sealed class ConcurrencyConflictException(string message) : Exception(message);

public sealed class EmailTakenException() : Exception("Email is already registered.");

public sealed class InvalidConfirmationTokenException() : Exception("The confirmation token is invalid, expired or already used.");

public sealed class InvalidCredentialsException() : Exception("Invalid email or password.");

public sealed class EmailNotConfirmedException() : Exception("Email is not confirmed.");

public sealed class UniqueViolationException(string constraintName) : Exception($"Unique constraint '{constraintName}' was violated.")
{
    public string ConstraintName { get; } = constraintName;
}
