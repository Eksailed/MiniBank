namespace MiniBank.Api;

public sealed record CreateAccountRequest(string Owner, string Currency);

public sealed record DepositRequest(long Amount, string Currency);

public sealed record TransferRequest(Guid FromAccountId, Guid ToAccountId, long Amount, string Currency);

// Поля аутентификации nullable: пустой или неполный ввод разбирает сервис и отвечает так же, как на неверный.
public sealed record RegisterRequest(string? Email, string? Password, string? DisplayName);

public sealed record ConfirmEmailRequest(string? Token);

public sealed record ResendConfirmationRequest(string? Email);

public sealed record LoginRequest(string? Email, string? Password);

public sealed record LoginResponse(string AccessToken, DateTimeOffset ExpiresAt);
