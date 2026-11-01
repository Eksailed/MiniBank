using MiniBank.Application;
using MiniBank.Domain;

namespace MiniBank.Api.Errors;

public static class ExceptionMapper
{
    public static int ToStatusCode(Exception ex) => ex switch
    {
        DomainValidationException or InvalidConfirmationTokenException => StatusCodes.Status400BadRequest,
        InvalidCredentialsException => StatusCodes.Status401Unauthorized,
        EmailNotConfirmedException => StatusCodes.Status403Forbidden,
        InsufficientFundsException or AccountBlockedException or TransferNotAllowedException
            or IdempotencyKeyReuseException or AccountLimitReachedException
            => StatusCodes.Status422UnprocessableEntity,
        AccountNotFoundException => StatusCodes.Status404NotFound,
        ConcurrencyConflictException or EmailTakenException => StatusCodes.Status409Conflict,
        _ => StatusCodes.Status500InternalServerError
    };

    /// <summary>Машинный код ошибки для поля <c>code</c> в <c>ProblemDetails</c>; <c>null</c>, если у ошибки нет отдельного кода.</summary>
    public static string? ToErrorCode(Exception ex) => ex switch
    {
        AccountLimitReachedException => "account_limit_reached",
        EmailTakenException => "email_taken",
        InvalidConfirmationTokenException => "invalid_confirmation_token",
        InvalidCredentialsException => "invalid_credentials",
        EmailNotConfirmedException => "email_not_confirmed",
        _ => null
    };
}
