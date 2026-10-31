using System.Net;
using System.Net.Http.Json;
using MiniBank.Application;
using MiniBank.Domain;
using MiniBank.TestSupport;

namespace MiniBank.Api.IntegrationTests;

[Collection("postgres")]
public class TransfersEndpointsTests(PostgresFixture postgres)
{
    private static async Task<AccountDto> CreateAccountAsync(HttpClient client, string owner, string currency = "RUB")
    {
        var response = await client.PostAsJsonAsync("/accounts", new { owner, currency });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<AccountDto>())!;
    }

    private static Task<HttpResponseMessage> DepositAsync(HttpClient client, Guid id, long amount, string currency = "RUB") =>
        client.PostWithKeyAsync($"/accounts/{id}/deposit", new { amount, currency });

    private static Task<HttpResponseMessage> TransferAsync(
        HttpClient client, Guid from, Guid to, long amount, string currency = "RUB") =>
        client.PostWithKeyAsync("/transfers", new { fromAccountId = from, toAccountId = to, amount, currency });

    private static async Task<long> BalanceAsync(HttpClient client, Guid id) =>
        (await client.GetFromJsonAsync<AccountDto>($"/accounts/{id}"))!.Balance;

    [Fact]
    public async Task Deposit_Returns200AndIncreasesBalance()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        using var client = factory.CreateClient();
        var account = await CreateAccountAsync(client, "Alice");

        var response = await DepositAsync(client, account.Id, 1000);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var transfer = await response.Content.ReadFromJsonAsync<TransferDto>();
        Assert.Equal(1000, transfer!.Amount);
        Assert.Equal(account.Id, transfer.ToAccountId);
        Assert.Equal(1000, await BalanceAsync(client, account.Id));
    }

    [Fact]
    public async Task Deposit_UnknownAccount_Returns404()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        using var client = factory.CreateClient();

        var response = await DepositAsync(client, Guid.NewGuid(), 100);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public async Task Deposit_NonPositiveAmount_Returns400(long amount)
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        using var client = factory.CreateClient();
        var account = await CreateAccountAsync(client, "Alice");

        var response = await DepositAsync(client, account.Id, amount);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, await BalanceAsync(client, account.Id));
    }

    [Fact]
    public async Task Transfer_Returns200AndMovesMoney()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        using var client = factory.CreateClient();
        var a = await CreateAccountAsync(client, "Alice");
        var b = await CreateAccountAsync(client, "Bob");
        await DepositAsync(client, a.Id, 1000);

        var response = await TransferAsync(client, a.Id, b.Id, 300);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(700, await BalanceAsync(client, a.Id));
        Assert.Equal(300, await BalanceAsync(client, b.Id));
    }

    [Fact]
    public async Task Transfer_InsufficientFunds_Returns422()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        using var client = factory.CreateClient();
        var a = await CreateAccountAsync(client, "Alice");
        var b = await CreateAccountAsync(client, "Bob");
        await DepositAsync(client, a.Id, 100);

        var response = await TransferAsync(client, a.Id, b.Id, 101);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(100, await BalanceAsync(client, a.Id));
    }

    [Fact]
    public async Task Transfer_ToSameAccount_Returns400()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        using var client = factory.CreateClient();
        var a = await CreateAccountAsync(client, "Alice");
        await DepositAsync(client, a.Id, 100);

        var response = await TransferAsync(client, a.Id, a.Id, 10);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Transfer_CurrencyMismatch_Returns400()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        using var client = factory.CreateClient();
        var rub = await CreateAccountAsync(client, "Alice", "RUB");
        var usd = await CreateAccountAsync(client, "Bob", "USD");
        await DepositAsync(client, rub.Id, 100);

        var response = await TransferAsync(client, rub.Id, usd.Id, 10);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(100, await BalanceAsync(client, rub.Id));
    }

    [Fact]
    public async Task Transfer_UnknownAccount_Returns404()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        using var client = factory.CreateClient();
        var a = await CreateAccountAsync(client, "Alice");
        await DepositAsync(client, a.Id, 100);

        var response = await TransferAsync(client, a.Id, Guid.NewGuid(), 10);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("""{"amount":10,"currency":"RUB"}""")]
    [InlineData("""{"toAccountId":"11111111-1111-1111-1111-111111111111","amount":10,"currency":"RUB"}""")]
    [InlineData("""{"fromAccountId":"11111111-1111-1111-1111-111111111111","amount":10,"currency":"RUB"}""")]
    [InlineData("{ not json")]
    [InlineData("")]
    public async Task Transfer_MissingFieldsOrBrokenBody_Returns400(string body)
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        using var client = factory.CreateClient();

        var response = await client.PostRawWithKeyAsync("/transfers", body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("""{"currency":"RUB"}""")]
    [InlineData("{ not json")]
    public async Task Deposit_MissingFieldsOrBrokenBody_Returns400(string body)
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        using var client = factory.CreateClient();
        var account = await CreateAccountAsync(client, "Alice");

        var response = await client.PostRawWithKeyAsync($"/accounts/{account.Id}/deposit", body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Transfer_WithCashAccount_Returns422()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        using var client = factory.CreateClient();
        var a = await CreateAccountAsync(client, "Alice");
        await DepositAsync(client, a.Id, 100);
        var cash = SystemAccounts.CashId("RUB");

        var fromCash = await TransferAsync(client, cash, a.Id, 10);
        var toCash = await TransferAsync(client, a.Id, cash, 10);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, fromCash.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, toCash.StatusCode);
        Assert.Equal(100, await BalanceAsync(client, a.Id));
        Assert.Equal(-100, await BalanceAsync(client, cash));
    }

    [Fact]
    public async Task Transfer_HugeAmount_Returns400Not500()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        using var client = factory.CreateClient();
        var sender = await CreateAccountAsync(client, "Alice");
        var receiver = await CreateAccountAsync(client, "Bob");
        await DepositAsync(client, sender.Id, 2);
        await DepositAsync(client, receiver.Id, long.MaxValue - 1);

        var response = await TransferAsync(client, sender.Id, receiver.Id, 2);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(2, await BalanceAsync(client, sender.Id));
        Assert.Equal(long.MaxValue - 1, await BalanceAsync(client, receiver.Id));
    }

    [Fact]
    public async Task Entries_ReturnsHistoryNewestFirst()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        using var client = factory.CreateClient();
        var a = await CreateAccountAsync(client, "Alice");
        var b = await CreateAccountAsync(client, "Bob");
        await DepositAsync(client, a.Id, 1000);
        var transfer = await (await TransferAsync(client, a.Id, b.Id, 300)).Content.ReadFromJsonAsync<TransferDto>();

        var entries = await client.GetFromJsonAsync<List<EntryDto>>($"/accounts/{a.Id}/entries");

        Assert.Equal(2, entries!.Count);
        Assert.Equal(transfer!.Id, entries[0].TransferId);
        Assert.Equal(-300, entries[0].Amount);
        Assert.Equal(700, entries[0].BalanceAfter);
        Assert.Equal(1000, entries[1].Amount);
    }

    [Fact]
    public async Task Entries_DefaultsAndBounds()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        using var client = factory.CreateClient();
        var a = await CreateAccountAsync(client, "Alice");

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/accounts/{a.Id}/entries")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"/accounts/{a.Id}/entries?page=0")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"/accounts/{a.Id}/entries?pageSize=0")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"/accounts/{a.Id}/entries?pageSize=101")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"/accounts/{a.Id}/entries?page=21474838&pageSize=100")).StatusCode);
    }

    [Fact]
    public async Task Entries_UnknownAccount_Returns404()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"/accounts/{Guid.NewGuid()}/entries");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
