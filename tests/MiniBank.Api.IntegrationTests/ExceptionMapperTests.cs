using MiniBank.Api.Errors;
using MiniBank.Application;
using MiniBank.Domain;

namespace MiniBank.Api.IntegrationTests;

public class ExceptionMapperTests
{
    public static TheoryData<Exception, int> Cases => new()
    {
        { new DomainValidationException("bad"), 400 },
        { new InsufficientFundsException("poor"), 422 },
        { new AccountBlockedException("blocked"), 422 },
        { new TransferNotAllowedException("no"), 422 },
        { new AccountNotFoundException(Guid.NewGuid()), 404 },
        { new ConcurrencyConflictException("conflict"), 409 },
        { new IdempotencyKeyReuseException("reused"), 422 },
        { new InvalidResetTokenException(), 400 },
        { new NotificationsUnavailableException(), 503 },
        { new InvalidOperationException("boom"), 500 }
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void ToStatusCode_MapsEachExceptionType(Exception exception, int expected) =>
        Assert.Equal(expected, ExceptionMapper.ToStatusCode(exception));
}
