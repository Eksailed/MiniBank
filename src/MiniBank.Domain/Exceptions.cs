namespace MiniBank.Domain;

public abstract class DomainException(string message) : Exception(message);

public sealed class DomainValidationException(string message) : DomainException(message);

public sealed class InsufficientFundsException(string message) : DomainException(message);

public sealed class AccountBlockedException(string message) : DomainException(message);
