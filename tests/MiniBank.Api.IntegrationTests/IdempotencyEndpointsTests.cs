using System.Net;
using System.Net.Http.Json;
using MiniBank.Application;
using MiniBank.TestSupport;

namespace MiniBank.Api.IntegrationTests;

[Collection("postgres")]
public class IdempotencyEndpointsTests(PostgresFixture postgres)
{
    private const string Replayed = "Idempotent-Replayed";

    private static async Task<AccountDto> CreateAccountAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/accounts", new { currency = "RUB" });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<AccountDto>())!;
    }

    private static Task<HttpResponseMessage> DepositAsync(HttpClient client, Guid id, long amount, string? key) =>
        client.PostWithKeyAsync($"/accounts/{id}/deposit", new { amount, currency = "RUB" }, key);

    private static Task<HttpResponseMessage> TransferAsync(HttpClient client, Guid from, Guid to, long amount, string? key) =>
        client.PostWithKeyAsync(
            "/transfers", new { fromAccountId = from, toAccountId = to, amount, currency = "RUB" }, key);

    private static async Task<long> BalanceAsync(HttpClient client, Guid id) =>
        (await client.GetFromJsonAsync<AccountDto>($"/accounts/{id}"))!.Balance;

    private static async Task<int> EntryCountAsync(HttpClient client, Guid id) =>
        (await client.GetFromJsonAsync<List<EntryDto>>($"/accounts/{id}/entries?pageSize=100"))!.Count;

    [Fact]
    public async Task Deposit_WithoutKey_Returns400ProblemDetails()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        using var client = await TestUsers.NewSignedInClientAsync(factory);
        var account = await CreateAccountAsync(client);

        var response = await client.PostAsJsonAsync($"/accounts/{account.Id}/deposit", new { amount = 100, currency = "RUB" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(0, await BalanceAsync(client, account.Id));
    }

    [Fact]
    public async Task Transfer_WithoutKey_Returns400()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        using var client = await TestUsers.NewSignedInClientAsync(factory);
        var alice = await CreateAccountAsync(client);
        var bob = await CreateAccountAsync(client);
        (await DepositAsync(client, alice.Id, 500, null)).EnsureSuccessStatusCode();

        var response = await client.PostAsJsonAsync(
            "/transfers", new { fromAccountId = alice.Id, toAccountId = bob.Id, amount = 100, currency = "RUB" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(500, await BalanceAsync(client, alice.Id));
    }

    public static TheoryData<string> InvalidKeys => new() { "   ", new string('k', 201) };

    [Theory]
    [MemberData(nameof(InvalidKeys))]
    public async Task Key_BlankOrTooLong_Returns400(string key)
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        using var client = await TestUsers.NewSignedInClientAsync(factory);
        var account = await CreateAccountAsync(client);

        var response = await DepositAsync(client, account.Id, 100, key);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, await BalanceAsync(client, account.Id));
    }

    [Fact]
    public async Task Deposit_RepeatedKey_ReturnsSameBodyAndReplayedHeader()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        using var client = await TestUsers.NewSignedInClientAsync(factory);
        var account = await CreateAccountAsync(client);

        var first = await DepositAsync(client, account.Id, 250, "dep-1");
        var second = await DepositAsync(client, account.Id, 250, "dep-1");

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.False(first.Headers.Contains(Replayed));
        Assert.Equal("true", Assert.Single(second.Headers.GetValues(Replayed)));
        Assert.Equal(await first.Content.ReadAsStringAsync(), await second.Content.ReadAsStringAsync());
        Assert.Equal(250, await BalanceAsync(client, account.Id));
        Assert.Equal(1, await EntryCountAsync(client, account.Id));
    }

    [Fact]
    public async Task Transfer_RepeatedKey_MovesMoneyOnce()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        using var client = await TestUsers.NewSignedInClientAsync(factory);
        var alice = await CreateAccountAsync(client);
        var bob = await CreateAccountAsync(client);
        (await DepositAsync(client, alice.Id, 1000, null)).EnsureSuccessStatusCode();

        var first = await TransferAsync(client, alice.Id, bob.Id, 300, "tr-1");
        var second = await TransferAsync(client, alice.Id, bob.Id, 300, "tr-1");

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.False(first.Headers.Contains(Replayed));
        Assert.Equal("true", Assert.Single(second.Headers.GetValues(Replayed)));
        Assert.Equal(await first.Content.ReadAsStringAsync(), await second.Content.ReadAsStringAsync());
        Assert.Equal(700, await BalanceAsync(client, alice.Id));
        Assert.Equal(300, await BalanceAsync(client, bob.Id));
        Assert.Equal(2, await EntryCountAsync(client, alice.Id));
        Assert.Equal(1, await EntryCountAsync(client, bob.Id));
    }

    [Fact]
    public async Task Transfer_SameKeyDifferentBody_Returns422()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        using var client = await TestUsers.NewSignedInClientAsync(factory);
        var alice = await CreateAccountAsync(client);
        var bob = await CreateAccountAsync(client);
        (await DepositAsync(client, alice.Id, 1000, null)).EnsureSuccessStatusCode();
        (await TransferAsync(client, alice.Id, bob.Id, 300, "tr-1")).EnsureSuccessStatusCode();

        var response = await TransferAsync(client, alice.Id, bob.Id, 301, "tr-1");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(300, await BalanceAsync(client, bob.Id));
    }

    [Fact]
    public async Task Key_SharedBetweenDepositAndTransfer_Returns422()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        using var client = await TestUsers.NewSignedInClientAsync(factory);
        var alice = await CreateAccountAsync(client);
        var bob = await CreateAccountAsync(client);
        (await DepositAsync(client, alice.Id, 500, "shared")).EnsureSuccessStatusCode();

        var response = await TransferAsync(client, alice.Id, bob.Id, 500, "shared");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(500, await BalanceAsync(client, alice.Id));
        Assert.Equal(0, await BalanceAsync(client, bob.Id));
    }

    [Fact]
    public async Task Transfer_ParallelSameKey_CreatesOneTransfer()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        using var client = await TestUsers.NewSignedInClientAsync(factory);
        var alice = await CreateAccountAsync(client);
        var bob = await CreateAccountAsync(client);
        (await DepositAsync(client, alice.Id, 1000, null)).EnsureSuccessStatusCode();

        var responses = await ParallelRunner.RunAsync(
            Enumerable.Range(0, 20).Select(_ =>
                (Func<Task<HttpResponseMessage>>)(() => TransferAsync(client, alice.Id, bob.Id, 100, "parallel-1"))), 20);

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        var bodies = await Task.WhenAll(responses.Select(r => r.Content.ReadAsStringAsync()));
        Assert.Single(bodies.Distinct());
        Assert.Equal(1, responses.Count(r => !r.Headers.Contains(Replayed)));
        Assert.Equal(100, await BalanceAsync(client, bob.Id));
        Assert.Equal(900, await BalanceAsync(client, alice.Id));
    }

    [Fact]
    public async Task Transfer_FailedRequest_DoesNotHoldKey()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        using var client = await TestUsers.NewSignedInClientAsync(factory);
        var alice = await CreateAccountAsync(client);
        var bob = await CreateAccountAsync(client);

        var failed = await TransferAsync(client, alice.Id, bob.Id, 100, "retry-1");
        (await DepositAsync(client, alice.Id, 500, null)).EnsureSuccessStatusCode();
        var retried = await TransferAsync(client, alice.Id, bob.Id, 100, "retry-1");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, failed.StatusCode);
        Assert.Equal(HttpStatusCode.OK, retried.StatusCode);
        Assert.False(retried.Headers.Contains(Replayed));
        Assert.Equal(100, await BalanceAsync(client, bob.Id));
    }
}
