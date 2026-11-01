using System.Text;
using MiniBank.Application.Statements;
using MiniBank.Domain;
using MiniBank.TestSupport;

namespace MiniBank.Infrastructure.Tests;

public class StatementCsvTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 31, 12, 30, 45, TimeSpan.Zero);

    private static async IAsyncEnumerable<LedgerEntry> Rows(IEnumerable<LedgerEntry> entries)
    {
        foreach (var entry in entries)
        {
            await Task.Yield();
            yield return entry;
        }
    }

    private static async Task<string> WriteAsync(string currency, IEnumerable<LedgerEntry> entries)
    {
        await using var writer = new StringWriter();
        await StatementCsv.WriteAsync(writer, currency, Rows(entries), CancellationToken.None);
        return writer.ToString();
    }

    [Theory]
    [InlineData(123456, "1234.56")]
    [InlineData(100, "1.00")]
    [InlineData(5, "0.05")]
    [InlineData(0, "0.00")]
    [InlineData(-5, "-0.05")]
    [InlineData(-123456, "-1234.56")]
    [InlineData(long.MaxValue, "92233720368547758.07")]
    [InlineData(long.MinValue + 1, "-92233720368547758.07")]
    public void FormatAmount_ShowsMajorUnitsWithTwoDecimalsAndDot(long minorUnits, string expected)
    {
        var previous = Thread.CurrentThread.CurrentCulture;
        Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("ru-RU");
        try
        {
            Assert.Equal(expected, StatementCsv.FormatAmount(minorUnits));
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = previous;
        }
    }

    [Fact]
    public async Task Write_EmptyStatement_IsHeaderOnly()
    {
        var csv = await WriteAsync("RUB", []);

        Assert.Equal("Дата (UTC);ID перевода;Сумма;Валюта;Баланс после\r\n", csv);
    }

    [Fact]
    public async Task Write_OneRowPerEntry_WithSignedAmountAndBalance()
    {
        var user = Guid.NewGuid();
        var from = TestData.OpenFor(user);
        var to = TestData.OpenFor(user);
        from.Credit(Guid.NewGuid(), Money.Of(100_050, "RUB"), Now);
        var transfer = Transfer.Execute(from, to, Money.Of(25_005, "RUB"), Now.AddSeconds(7));

        var csv = await WriteAsync("RUB", transfer.Entries.Where(e => e.AccountId == from.Id));

        var lines = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        Assert.Equal($"2026-10-31 12:30:52;{transfer.Id};-250.05;RUB;750.45", lines[1]);
    }

    [Fact]
    public async Task Write_UsesSemicolonAndNoQuotes_SoCellsCannotCarryFormulas()
    {
        var user = Guid.NewGuid();
        var from = TestData.OpenFor(user);
        var to = TestData.OpenFor(user);
        from.Credit(Guid.NewGuid(), Money.Of(1_000, "RUB"), Now);
        var transfer = Transfer.Execute(from, to, Money.Of(1, "RUB"), Now);

        var csv = await WriteAsync("RUB", transfer.Entries);

        foreach (var line in csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Skip(1))
        {
            var cells = line.Split(';');
            Assert.Equal(5, cells.Length);
            Assert.All(cells, c => Assert.Matches("^[-0-9A-Za-z: .]+$", c));
        }
    }

    [Fact]
    public async Task Write_DoesNotWriteBom_TheControllerOwnsIt()
    {
        var csv = await WriteAsync("RUB", []);

        Assert.False(csv.StartsWith('﻿'));
        Assert.Equal(Encoding.UTF8.GetByteCount(csv), Encoding.UTF8.GetBytes(csv).Length);
    }

    [Fact]
    public void EntryFilter_FromNotBeforeTo_IsRejected()
    {
        var at = Now;

        Assert.Throws<DomainValidationException>(() => new EntryFilter(at, at, null));
        Assert.Throws<DomainValidationException>(() => new EntryFilter(at.AddSeconds(1), at, null));
        Assert.NotNull(new EntryFilter(at, at.AddTicks(1), null));
        Assert.NotNull(new EntryFilter(at, null, EntryDirection.Out));
        Assert.NotNull(new EntryFilter(null, at, EntryDirection.In));
    }
}
