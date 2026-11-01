using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MiniBank.Application;
using MiniBank.TestSupport;
using Npgsql;

namespace MiniBank.Api.IntegrationTests;

[Collection("postgres")]
public class OwnershipTests(PostgresFixture postgres)
{
    private static async Task<AccountDto> OpenAsync(HttpClient client, string currency = "RUB")
    {
        var response = await client.PostAsJsonAsync("/accounts", new { currency });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AccountDto>())!;
    }

    private static async Task DepositAsync(HttpClient client, Guid accountId, long amount)
    {
        var response = await client.PostWithKeyAsync($"/accounts/{accountId}/deposit", new { amount, currency = "RUB" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static async Task<string?> ErrorCodeAsync(HttpResponseMessage response)
    {
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null;
    }

    [Fact]
    public async Task Anonymous_ProtectedEndpoints_Return401_AndOpenOnesStayOpen()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        using var client = factory.CreateClient();
        var id = Guid.NewGuid();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/accounts")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/accounts", new { currency = "RUB" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync($"/accounts/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync($"/accounts/{id}/entries")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostWithKeyAsync(
            $"/accounts/{id}/deposit", new { amount = 1, currency = "RUB" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostWithKeyAsync(
            "/transfers", new { fromAccountId = id, toAccountId = Guid.NewGuid(), amount = 1, currency = "RUB" })).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.PostAsJsonAsync("/auth/confirm-email", new { token = "not-a-token" })).StatusCode);
    }

    [Fact]
    public async Task Create_TakesOwnerFromDisplayName_AndIgnoresOwnerInBody()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        using var client = await TestUsers.NewSignedInClientAsync(factory, name: "Alice Cooper");

        var response = await client.PostAsJsonAsync("/accounts", new { owner = "Mallory", currency = "RUB" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var account = await response.Content.ReadFromJsonAsync<AccountDto>();
        Assert.Equal("Alice Cooper", account!.Owner);
    }

    [Fact]
    public async Task List_ReturnsOnlyOwnAccounts_OldestFirst()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        using var alice = await TestUsers.NewSignedInClientAsync(factory);
        using var bob = await TestUsers.NewSignedInClientAsync(factory, name: "Bob");
        var first = await OpenAsync(alice);
        var second = await OpenAsync(alice, "USD");
        var bobs = await OpenAsync(bob);

        var aliceList = await alice.GetFromJsonAsync<List<AccountDto>>("/accounts");
        var bobList = await bob.GetFromJsonAsync<List<AccountDto>>("/accounts");

        Assert.Equal([first.Id, second.Id], aliceList!.Select(a => a.Id));
        Assert.Equal([bobs.Id], bobList!.Select(a => a.Id));
    }

    [Fact]
    public async Task ForeignAccount_Get_Returns404()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        using var alice = await TestUsers.NewSignedInClientAsync(factory);
        using var bob = await TestUsers.NewSignedInClientAsync(factory, name: "Bob");
        var bobs = await OpenAsync(bob);

        var response = await alice.GetAsync($"/accounts/{bobs.Id}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ForeignAccount_Entries_Returns404()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        using var alice = await TestUsers.NewSignedInClientAsync(factory);
        using var bob = await TestUsers.NewSignedInClientAsync(factory, name: "Bob");
        var bobs = await OpenAsync(bob);

        var response = await alice.GetAsync($"/accounts/{bobs.Id}/entries");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ForeignAccount_Deposit_Returns404_AndBalanceUnchanged()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        using var alice = await TestUsers.NewSignedInClientAsync(factory);
        using var bob = await TestUsers.NewSignedInClientAsync(factory, name: "Bob");
        var bobs = await OpenAsync(bob);

        var response = await alice.PostWithKeyAsync($"/accounts/{bobs.Id}/deposit", new { amount = 500, currency = "RUB" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(0, (await bob.GetFromJsonAsync<AccountDto>($"/accounts/{bobs.Id}"))!.Balance);
    }

    [Fact]
    public async Task Transfer_FromForeignAccount_Returns404_ToForeignAccount_Returns200()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        using var alice = await TestUsers.NewSignedInClientAsync(factory);
        using var bob = await TestUsers.NewSignedInClientAsync(factory, name: "Bob");
        var alices = await OpenAsync(alice);
        var bobs = await OpenAsync(bob);
        await DepositAsync(alice, alices.Id, 1000);
        await DepositAsync(bob, bobs.Id, 1000);

        var steal = await alice.PostWithKeyAsync("/transfers",
            new { fromAccountId = bobs.Id, toAccountId = alices.Id, amount = 100, currency = "RUB" });
        var pay = await alice.PostWithKeyAsync("/transfers",
            new { fromAccountId = alices.Id, toAccountId = bobs.Id, amount = 100, currency = "RUB" });

        Assert.Equal(HttpStatusCode.NotFound, steal.StatusCode);
        Assert.Equal(HttpStatusCode.OK, pay.StatusCode);
        Assert.Equal(1100, (await bob.GetFromJsonAsync<AccountDto>($"/accounts/{bobs.Id}"))!.Balance);
    }

    [Fact]
    public async Task IdempotencyKey_IsScopedPerUser_SameKeyDifferentBodyIsNotAConflict()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        using var alice = await TestUsers.NewSignedInClientAsync(factory);
        using var bob = await TestUsers.NewSignedInClientAsync(factory, name: "Bob");
        var alices = await OpenAsync(alice);
        var bobs = await OpenAsync(bob);

        var first = await alice.PostWithKeyAsync($"/accounts/{alices.Id}/deposit",
            new { amount = 100, currency = "RUB" }, key: "shared-key");
        var second = await bob.PostWithKeyAsync($"/accounts/{bobs.Id}/deposit",
            new { amount = 200, currency = "RUB" }, key: "shared-key");

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(200, (await bob.GetFromJsonAsync<AccountDto>($"/accounts/{bobs.Id}"))!.Balance);
    }

    [Fact]
    public async Task Transfer_ForeignSource404_DoesNotConsumeIdempotencyKey()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        using var alice = await TestUsers.NewSignedInClientAsync(factory);
        using var bob = await TestUsers.NewSignedInClientAsync(factory, name: "Bob");
        var alices = await OpenAsync(alice);
        var bobs = await OpenAsync(bob);
        await DepositAsync(alice, alices.Id, 1000);
        await DepositAsync(bob, bobs.Id, 1000);

        var steal = await alice.PostWithKeyAsync("/transfers",
            new { fromAccountId = bobs.Id, toAccountId = alices.Id, amount = 100, currency = "RUB" }, key: "retry-key");
        var own = await alice.PostWithKeyAsync("/transfers",
            new { fromAccountId = alices.Id, toAccountId = bobs.Id, amount = 100, currency = "RUB" }, key: "retry-key");

        Assert.Equal(HttpStatusCode.NotFound, steal.StatusCode);
        Assert.Equal(HttpStatusCode.OK, own.StatusCode);
    }

    [Fact]
    public async Task Create_51stAccount_Returns422AccountLimitReached()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        using var client = await TestUsers.NewSignedInClientAsync(factory);
        for (var i = 0; i < 50; i++)
            await OpenAsync(client);

        var response = await client.PostAsJsonAsync("/accounts", new { currency = "RUB" });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("account_limit_reached", await ErrorCodeAsync(response));
    }

    [Fact]
    public async Task Create_SixtyParallel_YieldsExactlyFiftyAccounts()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        using var client = await TestUsers.NewSignedInClientAsync(factory);

        var statuses = await ParallelRunner.RunAsync(
            Enumerable.Range(0, 60).Select<int, Func<Task<HttpStatusCode>>>(_ => async () =>
                (await client.PostAsJsonAsync("/accounts", new { currency = "RUB" })).StatusCode),
            limit: 20);

        Assert.Equal(50, statuses.Count(s => s == HttpStatusCode.Created));
        Assert.Equal(10, statuses.Count(s => s == HttpStatusCode.UnprocessableEntity));
        Assert.Equal(50, (await client.GetFromJsonAsync<List<AccountDto>>("/accounts"))!.Count);
    }

    [Fact]
    public async Task Idempotency_ForeignUserReusingKeyWithSameBody_Gets404_NotTheStoredResponse()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        using var alice = await TestUsers.NewSignedInClientAsync(factory);
        using var bob = await TestUsers.NewSignedInClientAsync(factory, name: "Bob");
        var alices = await OpenAsync(alice);
        var other = await OpenAsync(alice);
        await DepositAsync(alice, alices.Id, 1000);
        var body = new { fromAccountId = alices.Id, toAccountId = other.Id, amount = 100, currency = "RUB" };
        var original = await alice.PostWithKeyAsync("/transfers", body, key: "shared-key");
        Assert.Equal(HttpStatusCode.OK, original.StatusCode);
        var stored = await original.Content.ReadFromJsonAsync<TransferDto>();

        var replay = await bob.PostWithKeyAsync("/transfers", body, key: "shared-key");

        Assert.Equal(HttpStatusCode.NotFound, replay.StatusCode);
        Assert.DoesNotContain(stored!.Id.ToString(), await replay.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task LegacyAccountWithoutOwner_CannotBeDebited_ButCanReceive_AndIsNotListed()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        using var alice = await TestUsers.NewSignedInClientAsync(factory);
        using var bob = await TestUsers.NewSignedInClientAsync(factory, name: "Bob");
        var legacy = await OpenAsync(alice);
        var bobs = await OpenAsync(bob);
        await DepositAsync(alice, legacy.Id, 1000);
        await DepositAsync(bob, bobs.Id, 1000);
        // Как у счёта, открытого до миграций: ни владельца, ни лимитов; ограничения базы при этом остаются на месте.
        await ExecuteAsync(factory.ConnectionString,
            $"UPDATE accounts SET user_id = NULL, per_operation_limit = NULL, daily_limit = NULL WHERE id = '{legacy.Id}'");

        var debit = await alice.PostWithKeyAsync("/transfers",
            new { fromAccountId = legacy.Id, toAccountId = bobs.Id, amount = 100, currency = "RUB" });
        var credit = await bob.PostWithKeyAsync("/transfers",
            new { fromAccountId = bobs.Id, toAccountId = legacy.Id, amount = 100, currency = "RUB" });
        var list = await alice.GetFromJsonAsync<List<AccountDto>>("/accounts");
        var read = await alice.GetAsync($"/accounts/{legacy.Id}");
        var deposit = await alice.PostWithKeyAsync($"/accounts/{legacy.Id}/deposit", new { amount = 100, currency = "RUB" });

        Assert.Equal(HttpStatusCode.NotFound, debit.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, read.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, deposit.StatusCode);
        Assert.Equal(HttpStatusCode.OK, credit.StatusCode);
        Assert.Empty(list!);
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
}
