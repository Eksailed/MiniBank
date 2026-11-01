using System.Net;
using System.Net.Http.Json;
using MiniBank.Application;
using MiniBank.Domain;
using MiniBank.TestSupport;

namespace MiniBank.Api.IntegrationTests;

[Collection("postgres")]
public class ConcurrencyTests(PostgresFixture postgres)
{
    private static async Task<AccountDto> CreateAccountAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/accounts", new { currency = "RUB" });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<AccountDto>())!;
    }

    private static Task<HttpResponseMessage> DepositAsync(HttpClient client, Guid id, long amount) =>
        client.PostWithKeyAsync($"/accounts/{id}/deposit", new { amount, currency = "RUB" });

    private static async Task<AccountDto> GetAsync(HttpClient client, Guid id) =>
        (await client.GetFromJsonAsync<AccountDto>($"/accounts/{id}"))!;

    [Fact]
    public async Task ParallelDepositsToDifferentAccounts_AllSucceed()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        using var client = await TestUsers.NewSignedInClientAsync(factory);
        var accounts = new List<AccountDto>();
        for (var i = 0; i < 50; i++)
            accounts.Add(await CreateAccountAsync(client));

        var responses = await ParallelRunner.RunAsync(
            accounts.Select(a => (Func<Task<HttpResponseMessage>>)(() => DepositAsync(client, a.Id, 100))), 50);

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        foreach (var account in accounts)
            Assert.Equal(100, (await GetAsync(client, account.Id)).Balance);
        Assert.Equal(-5000, await Database.CashBalanceAsync(factory.ConnectionString));
    }

    [Fact]
    public async Task ParallelDepositsToSameAccount_AllSucceed()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        using var client = await TestUsers.NewSignedInClientAsync(factory);
        var account = await CreateAccountAsync(client);

        var responses = await ParallelRunner.RunAsync(
            Enumerable.Range(0, 50).Select(_ => (Func<Task<HttpResponseMessage>>)(() => DepositAsync(client, account.Id, 10))), 50);

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        Assert.Equal(500, (await GetAsync(client, account.Id)).Balance);
        var entries = await client.GetFromJsonAsync<List<EntryDto>>($"/accounts/{account.Id}/entries?pageSize=100");
        Assert.Equal(50, entries!.Count);
    }

    [Fact]
    public async Task OppositeTransfersDoNotDeadlock()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        using var client = await TestUsers.NewSignedInClientAsync(factory);
        var a = await CreateAccountAsync(client);
        var b = await CreateAccountAsync(client);
        (await DepositAsync(client, a.Id, 1000)).EnsureSuccessStatusCode();
        (await DepositAsync(client, b.Id, 1000)).EnsureSuccessStatusCode();

        var responses = await ParallelRunner.RunAsync(
            Enumerable.Range(0, 100).Select(i => (Func<Task<HttpResponseMessage>>)(() =>
                client.PostWithKeyAsync("/transfers", new
                {
                    fromAccountId = i % 2 == 0 ? a.Id : b.Id,
                    toAccountId = i % 2 == 0 ? b.Id : a.Id,
                    amount = 1,
                    currency = "RUB"
                }))), 16);

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        var total = (await GetAsync(client, a.Id)).Balance + (await GetAsync(client, b.Id)).Balance;
        Assert.Equal(2000, total);
    }
}
