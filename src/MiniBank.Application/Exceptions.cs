namespace MiniBank.Application;

public sealed class AccountNotFoundException(Guid id) : Exception($"Account {id} was not found.")
{
    public Guid AccountId { get; } = id;
}

public sealed class TransferNotAllowedException(string message) : Exception(message);

public sealed class ConcurrencyConflictException(string message) : Exception(message);
