using MiniBank.Domain;

namespace MiniBank.Domain.Tests;

public class MoneyTests
{
    [Fact]
    public void Of_NormalizesCurrencyToUpper()
    {
        Assert.Equal("RUB", Money.Of(100, " rub ").Currency);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Of_RejectsNonPositiveAmount(long amount)
    {
        Assert.Throws<DomainValidationException>(() => Money.Of(amount, "RUB"));
    }

    [Theory]
    [InlineData("JPY")]
    [InlineData("RU")]
    [InlineData("")]
    [InlineData(null)]
    public void Of_RejectsUnsupportedCurrency(string? currency)
    {
        Assert.Throws<DomainValidationException>(() => Money.Of(100, currency));
    }

    [Fact]
    public void Of_KeepsAmountInMinorUnits()
    {
        Assert.Equal(12345, Money.Of(12345, "USD").Amount);
    }
}
