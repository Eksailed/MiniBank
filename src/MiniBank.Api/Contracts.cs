namespace MiniBank.Api;

public sealed record CreateAccountRequest(string Owner, string Currency);

public sealed record DepositRequest(long Amount, string Currency);

public sealed record TransferRequest(Guid FromAccountId, Guid ToAccountId, long Amount, string Currency);
