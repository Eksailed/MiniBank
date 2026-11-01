using MiniBank.Application.Limits;
using MiniBank.Domain;

namespace MiniBank.Infrastructure.Tests;

public class LimitPolicyTests
{
    private static LimitPair Pair(long perOperation, long daily) => new() { PerOperation = perOperation, Daily = daily };

    private static LimitsOptions Options(
        LimitPair? rubDefault = null, LimitPair? rubMax = null, bool withUsd = true, bool withEur = true)
    {
        var defaults = new Dictionary<string, LimitPair> { ["RUB"] = rubDefault ?? Pair(100, 300) };
        var max = new Dictionary<string, LimitPair> { ["RUB"] = rubMax ?? Pair(1_000, 3_000) };
        if (withUsd)
        {
            defaults["USD"] = Pair(10, 30);
            max["USD"] = Pair(100, 300);
        }

        if (withEur)
        {
            defaults["EUR"] = Pair(10, 30);
            max["EUR"] = Pair(100, 300);
        }

        return new LimitsOptions { Default = defaults, Max = max };
    }

    [Fact]
    public void Default_AndMax_ComeFromConfigurationPerCurrency()
    {
        var policy = new LimitPolicy(Options());

        Assert.Equal(new AccountLimits(100, 300), policy.Default("RUB"));
        Assert.Equal(new AccountLimits(10, 30), policy.Default("USD"));
        Assert.Equal(new AccountLimits(1_000, 3_000), policy.Max("RUB"));
        Assert.Equal(new AccountLimits(100, 300), policy.Max("eur"));
    }

    [Fact]
    public void EnsureWithinMax_AcceptsLimitsExactlyAtMax()
    {
        new LimitPolicy(Options()).EnsureWithinMax("RUB", new AccountLimits(1_000, 3_000));
    }

    [Fact]
    public void EnsureWithinMax_RejectsPerOperationAboveMax()
    {
        var policy = new LimitPolicy(Options());

        Assert.Throws<DomainValidationException>(() => policy.EnsureWithinMax("RUB", new AccountLimits(1_001, 2_000)));
    }

    [Fact]
    public void EnsureWithinMax_RejectsDailyAboveMax()
    {
        var policy = new LimitPolicy(Options());

        Assert.Throws<DomainValidationException>(() => policy.EnsureWithinMax("RUB", new AccountLimits(10, 3_001)));
    }

    [Fact]
    public void EnsureWithinMax_UsesTheCurrencyOfTheAccount()
    {
        var policy = new LimitPolicy(Options());

        Assert.Throws<DomainValidationException>(() => policy.EnsureWithinMax("USD", new AccountLimits(101, 200)));
    }

    [Fact]
    public void Construction_DefaultAboveMax_Throws()
    {
        Assert.Throws<InvalidOperationException>(() =>
            new LimitPolicy(Options(rubDefault: Pair(2_000, 2_500))));
        Assert.Throws<InvalidOperationException>(() =>
            new LimitPolicy(Options(rubDefault: Pair(100, 4_000))));
    }

    [Fact]
    public void Construction_DefaultDailyBelowPerOperation_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => new LimitPolicy(Options(rubDefault: Pair(300, 100))));
    }

    [Fact]
    public void Construction_MaxDailyBelowPerOperation_Throws()
    {
        Assert.Throws<InvalidOperationException>(() =>
            new LimitPolicy(Options(rubDefault: Pair(10, 20), rubMax: Pair(500, 400))));
    }

    [Theory]
    [InlineData(0, 100)]
    [InlineData(100, 0)]
    [InlineData(-1, 100)]
    public void Construction_NonPositiveDefault_Throws(long perOperation, long daily)
    {
        Assert.Throws<InvalidOperationException>(() =>
            new LimitPolicy(Options(rubDefault: Pair(perOperation, daily))));
    }

    [Fact]
    public void Construction_MissingCurrency_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => new LimitPolicy(Options(withEur: false)));
    }

    [Fact]
    public void BuiltinOptions_AreConsistent()
    {
        var policy = new LimitPolicy(LimitsOptions.Builtin);

        foreach (var currency in Currencies.Supported)
            policy.EnsureWithinMax(currency, policy.Default(currency));
    }
}
