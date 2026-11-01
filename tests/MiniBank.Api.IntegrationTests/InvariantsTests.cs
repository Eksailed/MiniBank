using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MiniBank.Application;
using MiniBank.TestSupport;
using Npgsql;

namespace MiniBank.Api.IntegrationTests;

/// <summary>Сверка инвариантов леджера прямыми SQL-запросами, в обход сервисов.</summary>
[Collection("postgres")]
public class InvariantsTests(PostgresFixture postgres)
{
    private static async Task<AccountDto> CreateAccountAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/accounts", new { currency = "RUB" });
        return (await response.Content.ReadFromJsonAsync<AccountDto>())!;
    }

    private static async Task RunSeriesOfOperationsAsync(HttpClient client)
    {
        var accounts = new[]
        {
            await CreateAccountAsync(client),
            await CreateAccountAsync(client),
            await CreateAccountAsync(client)
        };
        foreach (var account in accounts)
            (await client.PostWithKeyAsync($"/accounts/{account.Id}/deposit", new { amount = 500, currency = "RUB" }))
                .EnsureSuccessStatusCode();

        var random = new Random(42);
        var insufficient = 0;
        for (var i = 0; i < 20; i++)
        {
            var from = accounts[random.Next(accounts.Length)];
            var to = accounts.Where(a => a.Id != from.Id).ElementAt(random.Next(accounts.Length - 1));
            var response = await client.PostWithKeyAsync("/transfers", new
            {
                fromAccountId = from.Id,
                toAccountId = to.Id,
                amount = random.Next(1, 400),
                currency = "RUB"
            });

            if (response.StatusCode == HttpStatusCode.UnprocessableEntity)
                insufficient++;
            else
                response.EnsureSuccessStatusCode();
        }

        Assert.True(insufficient < 20, "Expected at least some transfers to succeed.");
    }

    private static async Task<List<object[]>> QueryAsync(string connectionString, string sql, int columns)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<object[]>();
        while (await reader.ReadAsync())
        {
            var row = new object[columns];
            reader.GetValues(row);
            rows.Add(row);
        }

        return rows;
    }

    [Fact]
    public async Task AfterSeriesOfOperations_TotalOfAllBalancesIsZero()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        using var client = await TestUsers.NewSignedInClientAsync(factory);
        await RunSeriesOfOperationsAsync(client);

        var rows = await QueryAsync(factory.ConnectionString, "SELECT COALESCE(SUM(balance), 0) FROM accounts", 1);

        Assert.Equal(0m, Convert.ToDecimal(rows.Single()[0]));
    }

    [Fact]
    public async Task AfterSeriesOfOperations_BalanceEqualsSumOfEntries()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        using var client = await TestUsers.NewSignedInClientAsync(factory);
        await RunSeriesOfOperationsAsync(client);

        var rows = await QueryAsync(factory.ConnectionString, """
            SELECT a.id, a.balance, COALESCE(SUM(e.amount), 0)
            FROM accounts a LEFT JOIN ledger_entries e ON e.account_id = a.id
            GROUP BY a.id, a.balance
            """, 3);

        Assert.NotEmpty(rows);
        Assert.All(rows, row => Assert.Equal(Convert.ToDecimal(row[1]), Convert.ToDecimal(row[2])));
    }

    [Fact]
    public async Task EveryTransferHasExactlyTwoEntriesSummingToZero()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        using var client = await TestUsers.NewSignedInClientAsync(factory);
        await RunSeriesOfOperationsAsync(client);

        var rows = await QueryAsync(factory.ConnectionString, """
            SELECT t.id, COUNT(e.id), COALESCE(SUM(e.amount), 0)
            FROM transfers t LEFT JOIN ledger_entries e ON e.transfer_id = t.id
            GROUP BY t.id
            """, 3);

        Assert.NotEmpty(rows);
        Assert.All(rows, row =>
        {
            Assert.Equal(2L, Convert.ToInt64(row[1]));
            Assert.Equal(0m, Convert.ToDecimal(row[2]));
        });
    }

    [Fact]
    public async Task ConcurrentStorm_KeepsAllInvariants()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        using var client = await TestUsers.NewSignedInClientAsync(factory);
        var accounts = new List<AccountDto>();
        for (var i = 0; i < 5; i++)
        {
            var account = await CreateAccountAsync(client);
            (await client.PostWithKeyAsync($"/accounts/{account.Id}/deposit", new { amount = 10000, currency = "RUB" }))
                .EnsureSuccessStatusCode();
            accounts.Add(account);
        }

        var random = new Random(7);
        var requests = Enumerable.Range(0, 300).Select(_ =>
        {
            var from = accounts[random.Next(accounts.Count)];
            var to = accounts.Where(a => a.Id != from.Id).ElementAt(random.Next(accounts.Count - 1));
            var amount = random.Next(1, 501);
            return (Func<Task<HttpResponseMessage>>)(() => client.PostWithKeyAsync("/transfers",
                new { fromAccountId = from.Id, toAccountId = to.Id, amount, currency = "RUB" }));
        }).ToList();

        var responses = await ParallelRunner.RunAsync(requests, 16);

        Assert.All(responses, r => Assert.True(
            r.StatusCode is HttpStatusCode.OK or HttpStatusCode.UnprocessableEntity,
            $"Unexpected status {r.StatusCode}"));
        Assert.Contains(responses, r => r.StatusCode == HttpStatusCode.OK);

        var sum = await QueryAsync(factory.ConnectionString, "SELECT COALESCE(SUM(balance), 0) FROM accounts", 1);
        Assert.Equal(0m, Convert.ToDecimal(sum.Single()[0]));

        var balances = await QueryAsync(factory.ConnectionString, """
            SELECT a.id, a.balance, COALESCE(SUM(e.amount), 0), a.is_system
            FROM accounts a LEFT JOIN ledger_entries e ON e.account_id = a.id
            GROUP BY a.id, a.balance, a.is_system
            """, 4);
        Assert.All(balances, row =>
        {
            Assert.Equal(Convert.ToDecimal(row[1]), Convert.ToDecimal(row[2]));
            if (!(bool)row[3])
                Assert.True(Convert.ToDecimal(row[1]) >= 0);
        });

        var transfers = await QueryAsync(factory.ConnectionString, """
            SELECT t.id, COUNT(e.id), COALESCE(SUM(e.amount), 0)
            FROM transfers t LEFT JOIN ledger_entries e ON e.transfer_id = t.id
            GROUP BY t.id
            """, 3);
        Assert.NotEmpty(transfers);
        Assert.All(transfers, row =>
        {
            Assert.Equal(2L, Convert.ToInt64(row[1]));
            Assert.Equal(0m, Convert.ToDecimal(row[2]));
        });
    }

    [Fact]
    public async Task Swagger_ListsAllFiveEndpoints()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        using var client = await TestUsers.NewSignedInClientAsync(factory);

        using var document = JsonDocument.Parse(await client.GetStringAsync("/swagger/v1/swagger.json"));
        var paths = document.RootElement.GetProperty("paths").EnumerateObject().Select(p => p.Name).ToList();

        Assert.Contains("/accounts", paths);
        Assert.Contains("/accounts/{id}", paths);
        Assert.Contains("/accounts/{id}/deposit", paths);
        Assert.Contains("/accounts/{id}/entries", paths);
        Assert.Contains("/transfers", paths);
    }
}
