using MiniBank.Application;
using MiniBank.Domain;

namespace MiniBank.Api.Errors;

public static class ExceptionMapper
{
    public static int ToStatusCode(Exception ex) => ex switch
    {
        DomainValidationException => StatusCodes.Status400BadRequest,
        InsufficientFundsException or AccountBlockedException or TransferNotAllowedException
            or IdempotencyKeyReuseException
            => StatusCodes.Status422UnprocessableEntity,
        AccountNotFoundException => StatusCodes.Status404NotFound,
        ConcurrencyConflictException => StatusCodes.Status409Conflict,
        _ => StatusCodes.Status500InternalServerError
    };
}
