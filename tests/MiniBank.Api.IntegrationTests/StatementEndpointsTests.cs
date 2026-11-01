using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MiniBank.Application;
using MiniBank.TestSupport;

namespace MiniBank.Api.IntegrationTests;

[Collection("postgres")]
public class StatementEndpointsTests(PostgresFixture postgres)
{
    private static readonly DateTimeOffset T0 = new(2030, 5, 7, 10, 0, 0, TimeSpan.Zero);

    private static string Iso(DateTimeOffset at) => Uri.EscapeDataString(at.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ"));

    private static async Task<AccountDto> OpenAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/accounts", new { currency = "RUB" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AccountDto>())!;
    }

    private static async Task DepositAsync(HttpClient client, Guid id, long amount) =>
        Assert.Equal(HttpStatusCode.OK,
            (await client.PostWithKeyAsync($"/accounts/{id}/deposit", new { amount, currency = "RUB" })).StatusCode);

    private static async Task TransferAsync(HttpClient client, Guid from, Guid to, long amount) =>
        Assert.Equal(HttpStatusCode.OK, (await client.PostWithKeyAsync(
            "/transfers", new { fromAccountId = from, toAccountId = to, amount, currency = "RUB" })).StatusCode);

    private static async Task<List<EntryDto>> EntriesAsync(HttpClient client, Guid id, string query = "")
    {
        var response = await client.GetAsync($"/accounts/{id}/entries{query}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<List<EntryDto>>())!;
    }

    private static async Task<(HttpResponseMessage Response, byte[] Body)> CsvAsync(
        HttpClient client, Guid id, string query = "")
    {
        var response = await client.GetAsync($"/accounts/{id}/entries.csv{query}");
        return (response, await response.Content.ReadAsByteArrayAsync());
    }

    private static string[] Lines(byte[] body) =>
        Encoding.UTF8.GetString(body, 3, body.Length - 3).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

    /// <summary>Счёт с проводками +1000 в T0, +500 в T0+1ч и −200 в T0+2ч; часы ведёт тест.</summary>
    private async Task<(ApiFactory Factory, HttpClient Client, AccountDto Account)> SeededAsync(
        Dictionary<string, string?>? settings = null)
    {
        // Вход при реальном времени: JwtBearer проверяет токен по системным часам, а не по FakeClock.
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var factory = await ApiFactory.CreateAsync(postgres, settings: settings, configureServices: services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(clock);
        });
        var client = await TestUsers.NewSignedInClientAsync(factory);
        var account = await OpenAsync(client);
        var other = await OpenAsync(client);

        clock.Set(T0);
        await DepositAsync(client, account.Id, 1_000);
        clock.Set(T0.AddHours(1));
        await DepositAsync(client, account.Id, 500);
        clock.Set(T0.AddHours(2));
        await TransferAsync(client, account.Id, other.Id, 200);
        return (factory, client, account);
    }

    [Fact]
    public async Task From_IsInclusive_AndTo_IsExclusive()
    {
        var (factory, client, account) = await SeededAsync();
        await using var _ = factory;
        using var __ = client;

        var all = await EntriesAsync(client, account.Id);
        var fromFirst = await EntriesAsync(client, account.Id, $"?from={Iso(T0)}");
        var afterFirst = await EntriesAsync(client, account.Id, $"?from={Iso(T0.AddMicroseconds(1))}");
        var toSecond = await EntriesAsync(client, account.Id, $"?to={Iso(T0.AddHours(1))}");
        var window = await EntriesAsync(client, account.Id, $"?from={Iso(T0.AddHours(1))}&to={Iso(T0.AddHours(2))}");

        Assert.Equal([-200, 500, 1_000], all.Select(e => e.Amount));
        Assert.Equal([-200, 500, 1_000], fromFirst.Select(e => e.Amount));
        Assert.Equal([-200, 500], afterFirst.Select(e => e.Amount));
        Assert.Equal([1_000], toSecond.Select(e => e.Amount));
        Assert.Equal([500], window.Select(e => e.Amount));
    }

    [Fact]
    public async Task Dates_WithoutOffset_AreUtc_AndOffsetsAreConverted()
    {
        var (factory, client, account) = await SeededAsync();
        await using var _ = factory;
        using var __ = client;

        var noOffset = await EntriesAsync(client, account.Id, "?from=2030-05-07T11:00:00");
        var withOffset = await EntriesAsync(client, account.Id, $"?from={Uri.EscapeDataString("2030-05-07T14:00:00+03:00")}");
        var dateOnly = await EntriesAsync(client, account.Id, "?from=2030-05-08");

        Assert.Equal([-200, 500], noOffset.Select(e => e.Amount));
        Assert.Equal([-200, 500], withOffset.Select(e => e.Amount));
        Assert.Empty(dateOnly);
    }

    [Theory]
    [InlineData("in", new long[] { 500, 1_000 })]
    [InlineData("out", new long[] { -200 })]
    [InlineData("IN", new long[] { 500, 1_000 })]
    public async Task Direction_SelectsPositiveOrNegativeEntries(string direction, long[] expected)
    {
        var (factory, client, account) = await SeededAsync();
        await using var _ = factory;
        using var __ = client;

        var entries = await EntriesAsync(client, account.Id, $"?direction={direction}");

        Assert.Equal(expected, entries.Select(e => e.Amount));
    }

    [Fact]
    public async Task Filters_CombineWithPagination()
    {
        var (factory, client, account) = await SeededAsync();
        await using var _ = factory;
        using var __ = client;

        var page1 = await EntriesAsync(client, account.Id, "?direction=in&pageSize=1&page=1");
        var page2 = await EntriesAsync(client, account.Id, "?direction=in&pageSize=1&page=2");
        var page3 = await EntriesAsync(client, account.Id, "?direction=in&pageSize=1&page=3");
        var windowed = await EntriesAsync(client, account.Id, $"?direction=in&from={Iso(T0.AddHours(1))}&pageSize=5");

        Assert.Equal([500], page1.Select(e => e.Amount));
        Assert.Equal([1_000], page2.Select(e => e.Amount));
        Assert.Empty(page3);
        Assert.Equal([500], windowed.Select(e => e.Amount));
    }

    [Theory]
    [InlineData("from=yesterday")]
    [InlineData("to=31.12.2030")]
    [InlineData("direction=sideways")]
    [InlineData("from=2030-05-07T10:00:00Z&to=2030-05-07T10:00:00Z")]
    [InlineData("from=2030-05-08T10:00:00Z&to=2030-05-07T10:00:00Z")]
    public async Task InvalidFilter_Returns400_ForJsonAndCsv(string query)
    {
        var (factory, client, account) = await SeededAsync();
        await using var _ = factory;
        using var __ = client;

        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"/accounts/{account.Id}/entries?{query}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"/accounts/{account.Id}/entries.csv?{query}")).StatusCode);
    }

    [Fact]
    public async Task EmptyFilterValues_AreTreatedAsAbsent()
    {
        var (factory, client, account) = await SeededAsync();
        await using var _ = factory;
        using var __ = client;

        var entries = await EntriesAsync(client, account.Id, "?from=&to=&direction=");
        var (response, body) = await CsvAsync(client, account.Id, "?from=&to=&direction=");

        Assert.Equal(3, entries.Count);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(4, Lines(body).Length);
    }

    [Fact]
    public async Task ForeignAccount_JsonAndCsv_Return404()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        using var alice = await TestUsers.NewSignedInClientAsync(factory);
        using var bob = await TestUsers.NewSignedInClientAsync(factory, name: "Bob");
        var bobs = await OpenAsync(bob);
        await DepositAsync(bob, bobs.Id, 100);

        Assert.Equal(HttpStatusCode.NotFound, (await alice.GetAsync($"/accounts/{bobs.Id}/entries?direction=in")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await alice.GetAsync($"/accounts/{bobs.Id}/entries.csv")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await alice.GetAsync($"/accounts/{Guid.NewGuid()}/entries.csv")).StatusCode);
    }

    [Fact]
    public async Task Csv_Anonymous_Returns401()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"/accounts/{Guid.NewGuid()}/entries.csv");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Csv_IsUtf8WithBom_AttachmentWithSemicolonRows()
    {
        var (factory, client, account) = await SeededAsync();
        await using var _ = factory;
        using var __ = client;

        var (response, body) = await CsvAsync(client, account.Id);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/csv", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal("utf-8", response.Content.Headers.ContentType!.CharSet);
        Assert.Equal(
            $"attachment; filename=\"statement-{account.Id}.csv\"",
            response.Content.Headers.GetValues("Content-Disposition").Single());
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, body.Take(3).ToArray());
        Assert.False(Encoding.UTF8.GetString(body, 3, body.Length - 3).StartsWith('﻿'));

        var lines = Lines(body);
        Assert.Equal("Дата (UTC);ID перевода;Сумма;Валюта;Баланс после", lines[0]);
        Assert.Equal(4, lines.Length);
        Assert.All(lines, l => Assert.Equal(5, l.Split(';').Length));
    }

    [Fact]
    public async Task Csv_HasOneRowPerEntry_NewestFirst_WithDecimalAmounts()
    {
        var (factory, client, account) = await SeededAsync();
        await using var _ = factory;
        using var __ = client;
        var entries = await EntriesAsync(client, account.Id);

        var (_, body) = await CsvAsync(client, account.Id);

        var rows = Lines(body).Skip(1).Select(l => l.Split(';')).ToList();
        Assert.Equal(entries.Count, rows.Count);
        Assert.Equal(["-2.00", "5.00", "10.00"], rows.Select(r => r[2]));
        Assert.Equal(["13.00", "15.00", "10.00"], rows.Select(r => r[4]));
        Assert.Equal(["2030-05-07 12:00:00", "2030-05-07 11:00:00", "2030-05-07 10:00:00"], rows.Select(r => r[0]));
        Assert.Equal(entries.Select(e => e.TransferId.ToString()), rows.Select(r => r[1]));
        Assert.All(rows, r => Assert.Equal("RUB", r[3]));
    }

    [Fact]
    public async Task Csv_AppliesTheSameFilters()
    {
        var (factory, client, account) = await SeededAsync();
        await using var _ = factory;
        using var __ = client;

        var (_, outgoing) = await CsvAsync(client, account.Id, "?direction=out");
        var (_, window) = await CsvAsync(client, account.Id, $"?from={Iso(T0.AddHours(1))}&to={Iso(T0.AddHours(2))}");

        Assert.Equal(["-2.00"], Lines(outgoing).Skip(1).Select(l => l.Split(';')[2]));
        Assert.Equal(["5.00"], Lines(window).Skip(1).Select(l => l.Split(';')[2]));
    }

    [Fact]
    public async Task Csv_EmptyStatement_IsHeaderOnly()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        using var client = await TestUsers.NewSignedInClientAsync(factory);
        var account = await OpenAsync(client);

        var (response, body) = await CsvAsync(client, account.Id);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, body.Take(3).ToArray());
        Assert.Equal(["Дата (UTC);ID перевода;Сумма;Валюта;Баланс после"], Lines(body));
    }

    [Fact]
    public async Task Csv_OverTheRowCap_Returns400BeforeAnyBody_AtTheCapReturns200()
    {
        var (factory, client, account) = await SeededAsync(new() { ["Statements:MaxCsvRows"] = "2" });
        await using var _ = factory;
        using var __ = client;

        var over = await client.GetAsync($"/accounts/{account.Id}/entries.csv");
        var atCap = await client.GetAsync($"/accounts/{account.Id}/entries.csv?direction=in");

        Assert.Equal(HttpStatusCode.BadRequest, over.StatusCode);
        Assert.Equal("application/problem+json", over.Content.Headers.ContentType!.MediaType);
        Assert.Equal(HttpStatusCode.OK, atCap.StatusCode);
        Assert.Equal(3, Lines(await atCap.Content.ReadAsByteArrayAsync()).Length);
    }

    [Fact]
    public async Task Csv_RowCap_CountsOnlyEntriesMatchingTheFilter()
    {
        var (factory, client, account) = await SeededAsync(new() { ["Statements:MaxCsvRows"] = "1" });
        await using var _ = factory;
        using var __ = client;

        var narrowed = await client.GetAsync($"/accounts/{account.Id}/entries.csv?direction=out");
        var wide = await client.GetAsync($"/accounts/{account.Id}/entries.csv?direction=in");

        Assert.Equal(HttpStatusCode.OK, narrowed.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, wide.StatusCode);
    }
}
