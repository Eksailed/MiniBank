using System.Globalization;
using MiniBank.Domain;

namespace MiniBank.Application.Statements;

/// <summary>
/// Формат CSV-выписки. Все ячейки — даты, идентификаторы, числа и код валюты, свободного текста нет,
/// поэтому ни кавычки, ни защита от формул (<c>=</c>, <c>+</c>, <c>@</c>) не нужны.
/// </summary>
public static class StatementCsv
{
    /// <summary>Потолок строк по умолчанию (настраивается <c>Statements:MaxCsvRows</c>).</summary>
    public const int MaxRows = 50_000;

    public const string Header = "Дата (UTC);ID перевода;Сумма;Валюта;Баланс после";

    /// <summary>Сумма в основных единицах: <c>123456</c> → <c>1234.56</c>, всегда с точкой и двумя знаками.</summary>
    public static string FormatAmount(long minorUnits) =>
        (minorUnits / 100m).ToString("0.00", CultureInfo.InvariantCulture);

    public static async Task WriteAsync(
        TextWriter writer, string currency, IAsyncEnumerable<LedgerEntry> rows, CancellationToken ct)
    {
        await writer.WriteAsync(Header + "\r\n");
        await foreach (var row in rows.WithCancellation(ct))
        {
            await writer.WriteAsync(string.Join(';',
                row.CreatedAt.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                row.TransferId,
                FormatAmount(row.Amount),
                currency,
                FormatAmount(row.BalanceAfter)) + "\r\n");
        }
    }
}
