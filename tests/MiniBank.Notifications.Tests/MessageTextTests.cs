namespace MiniBank.Notifications.Tests;

public class MessageTextTests
{
    [Fact]
    public void FormatAmount_RubUsesSymbolAndNonBreakingThousands()
        => Assert.Equal("2 500,00 ₽", MessageText.FormatAmount(250_000, "RUB"));

    [Fact]
    public void FormatAmount_UsdPadsMinorUnits()
        => Assert.Equal("0,05 $", MessageText.FormatAmount(5, "USD"));

    [Fact]
    public void FormatAmount_UnknownCurrencyUsesCode()
        => Assert.Equal("1,00 XYZ", MessageText.FormatAmount(100, "XYZ"));

    [Fact]
    public void TransferSent_HasMinus()
        => Assert.Equal("Перевод: −2 500,00 ₽", MessageText.TransferSent(250_000, "RUB"));

    [Fact]
    public void TransferReceived_HasPlus()
        => Assert.Equal("Поступление: +2 500,00 ₽", MessageText.TransferReceived(250_000, "RUB"));

    [Fact]
    public void Deposit_HasPlus()
        => Assert.Equal("Пополнение: +10 000,00 ₽", MessageText.Deposit(1_000_000, "RUB"));
}
