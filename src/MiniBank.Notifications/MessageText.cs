using System.Text;

namespace MiniBank.Notifications;

/// <summary>Тексты уведомлений: без имён, email и идентификаторов, сумма из минорных единиц.</summary>
public static class MessageText
{
    private const char NonBreakingSpace = (char)0xA0;

    public static string TransferSent(long amount, string currency) => $"Перевод: −{FormatAmount(amount, currency)}";

    public static string TransferReceived(long amount, string currency) => $"Поступление: +{FormatAmount(amount, currency)}";

    public static string Deposit(long amount, string currency) => $"Пополнение: +{FormatAmount(amount, currency)}";

    public static string FormatAmount(long amount, string currency)
    {
        var abs = amount < 0 ? -(decimal)amount : amount;
        var whole = (long)(abs / 100);
        var fraction = (int)(abs % 100);

        var digits = whole.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var grouped = new StringBuilder();
        for (var i = 0; i < digits.Length; i++)
        {
            if (i > 0 && (digits.Length - i) % 3 == 0)
                grouped.Append(NonBreakingSpace);
            grouped.Append(digits[i]);
        }

        var symbol = currency switch { "RUB" => "₽", "USD" => "$", "EUR" => "€", _ => currency };
        return $"{(amount < 0 ? "−" : "")}{grouped},{fraction:00} {symbol}";
    }
}
