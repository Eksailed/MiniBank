using MiniBank.Domain;

namespace MiniBank.Domain.Tests;

public class AccountLimitsTests
{
    [Fact]
    public void Constructor_AcceptsPositiveLimits_AndDailyEqualToPerOperation()
    {
        var limits = new AccountLimits(100, 100);

        Assert.Equal(100, limits.PerOperation);
        Assert.Equal(100, limits.Daily);
    }

    [Theory]
    [InlineData(0, 100)]
    [InlineData(-1, 100)]
    [InlineData(100, 0)]
    [InlineData(100, -5)]
    [InlineData(0, 0)]
    public void Constructor_RejectsNonPositiveLimits(long perOperation, long daily)
    {
        Assert.Throws<DomainValidationException>(() => new AccountLimits(perOperation, daily));
    }

    [Fact]
    public void Constructor_RejectsDailyBelowPerOperation()
    {
        Assert.Throws<DomainValidationException>(() => new AccountLimits(101, 100));
    }
}
