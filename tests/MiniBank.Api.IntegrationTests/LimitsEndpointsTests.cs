using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MiniBank.Application;
using MiniBank.TestSupport;

namespace MiniBank.Api.IntegrationTests;

[Collection("postgres")]
public class LimitsEndpointsTests(PostgresFixture postgres)
{
    private static async Task<AccountDto> OpenAsync(HttpClient client, string currency = "RUB")
    {
        var response = await client.PostAsJsonAsync("/accounts", new { currency });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AccountDto>())!;
    }

    private static async Task<HttpResponseMessage> PutLimitsAsync(HttpClient client, Guid id, long perOperation, long daily) =>
        await client.PutAsJsonAsync($"/accounts/{id}/limits", new { perOperation, daily });

    private static async Task SetLimitsAsync(HttpClient client, Guid id, long perOperation, long daily) =>
        Assert.Equal(HttpStatusCode.OK, (await PutLimitsAsync(client, id, perOperation, daily)).StatusCode);

    private static async Task<LimitsDto> GetLimitsAsync(HttpClient client, Guid id)
    {
        var response = await client.GetAsync($"/accounts/{id}/limits");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<LimitsDto>())!;
    }

    private static async Task DepositAsync(HttpClient client, Guid id, long amount, string currency = "RUB") =>
        Assert.Equal(HttpStatusCode.OK,
            (await client.PostWithKeyAsync($"/accounts/{id}/deposit", new { amount, currency })).StatusCode);

    private static Task<HttpResponseMessage> TransferAsync(
        HttpClient client, Guid from, Guid to, long amount, string? key = null, string currency = "RUB") =>
        client.PostWithKeyAsync(
            "/transfers", new { fromAccountId = from, toAccountId = to, amount, currency }, key);

    private static async Task<JsonElement> ErrorAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    private static async Task<string?> ErrorCodeAsync(HttpResponseMessage response) =>
        (await ErrorAsync(response)).TryGetProperty("code", out var code) ? code.GetString() : null;

    [Fact]
    public async Task NewAccount_GetsDefaultLimitsOfItsCurrency()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        using var client = await TestUsers.NewSignedInClientAsync(factory);

        var rub = await GetLimitsAsync(client, (await OpenAsync(client)).Id);
        var usd = await GetLimitsAsync(client, (await OpenAsync(client, "USD")).Id);

        Assert.Equal(10_000_000, rub.PerOperation);
        Assert.Equal(30_000_000, rub.Daily);
        Assert.Equal(100_000_000, rub.MaxPerOperation);
        Assert.Equal(300_000_000, rub.MaxDaily);
        Assert.Equal(100_000, usd.PerOperation);
        Assert.Equal(300_000, usd.Daily);
        Assert.Equal(1_000_000, usd.MaxPerOperation);
        Assert.Equal(3_000_000, usd.MaxDaily);
    }

    [Fact]
    public async Task GetLimits_ReturnsSpentAndRemainingToday()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        using var client = await TestUsers.NewSignedInClientAsync(factory);
        var from = await OpenAsync(client);
        var to = await OpenAsync(client);
        await SetLimitsAsync(client, from.Id, 1_000, 3_000);
        await DepositAsync(client, from.Id, 10_000);
        Assert.Equal(HttpStatusCode.OK, (await TransferAsync(client, from.Id, to.Id, 400)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await TransferAsync(client, from.Id, to.Id, 100)).StatusCode);

        var limits = await GetLimitsAsync(client, from.Id);

        Assert.Equal(1_000, limits.PerOperation);
        Assert.Equal(3_000, limits.Daily);
        Assert.Equal(500, limits.SpentToday);
        Assert.Equal(2_500, limits.RemainingToday);
    }

    [Fact]
    public async Task PutLimits_UpdatesLimits_AndAcceptsTheCeiling()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        using var client = await TestUsers.NewSignedInClientAsync(factory);
        var account = await OpenAsync(client);

        var put = await PutLimitsAsync(client, account.Id, 500, 900);
        var atCeiling = await PutLimitsAsync(client, account.Id, 100_000_000, 300_000_000);

        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var updated = await put.Content.ReadFromJsonAsync<LimitsDto>();
        Assert.Equal(500, updated!.PerOperation);
        Assert.Equal(900, updated.Daily);
        Assert.Equal(HttpStatusCode.OK, atCeiling.StatusCode);
        Assert.Equal(300_000_000, (await GetLimitsAsync(client, account.Id)).Daily);
    }

    [Theory]
    [InlineData(0, 100)]
    [InlineData(-1, 100)]
    [InlineData(100, 0)]
    [InlineData(100, -1)]
    [InlineData(200, 100)]
    [InlineData(100_000_001, 200_000_000)]
    [InlineData(10, 300_000_001)]
    public async Task PutLimits_Invalid_Returns400_AndKeepsLimits(long perOperation, long daily)
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        using var client = await TestUsers.NewSignedInClientAsync(factory);
        var account = await OpenAsync(client);

        var response = await PutLimitsAsync(client, account.Id, perOperation, daily);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var limits = await GetLimitsAsync(client, account.Id);
        Assert.Equal(10_000_000, limits.PerOperation);
        Assert.Equal(30_000_000, limits.Daily);
    }

    [Fact]
    public async Task ForeignAccount_GetAndPutLimits_Return404_AndLimitsStayUntouched()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        using var alice = await TestUsers.NewSignedInClientAsync(factory);
        using var bob = await TestUsers.NewSignedInClientAsync(factory, name: "Bob");
        var bobs = await OpenAsync(bob);

        var get = await alice.GetAsync($"/accounts/{bobs.Id}/limits");
        var put = await PutLimitsAsync(alice, bobs.Id, 1, 1);

        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, put.StatusCode);
        Assert.Equal(10_000_000, (await GetLimitsAsync(bob, bobs.Id)).PerOperation);
    }

    [Fact]
    public async Task Anonymous_Limits_Return401()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        using var client = factory.CreateClient();
        var id = Guid.NewGuid();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync($"/accounts/{id}/limits")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await PutLimitsAsync(client, id, 1, 1)).StatusCode);
    }

    [Fact]
    public async Task Transfer_AbovePerOperationLimit_Returns422_WithLimit_AndNothingMoves()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        using var client = await TestUsers.NewSignedInClientAsync(factory);
        var from = await OpenAsync(client);
        var to = await OpenAsync(client);
        await SetLimitsAsync(client, from.Id, 1_000, 5_000);
        await DepositAsync(client, from.Id, 10_000);

        var response = await TransferAsync(client, from.Id, to.Id, 1_001);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var error = await ErrorAsync(response);
        Assert.Equal("per_operation_limit_exceeded", error.GetProperty("code").GetString());
        Assert.Equal(1_000, error.GetProperty("limit").GetInt64());
        Assert.Equal(10_000, (await client.GetFromJsonAsync<AccountDto>($"/accounts/{from.Id}"))!.Balance);
        Assert.Equal(0, (await GetLimitsAsync(client, from.Id)).SpentToday);
    }

    [Fact]
    public async Task Transfer_AtPerOperationLimit_Passes()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        using var client = await TestUsers.NewSignedInClientAsync(factory);
        var from = await OpenAsync(client);
        var to = await OpenAsync(client);
        await SetLimitsAsync(client, from.Id, 1_000, 5_000);
        await DepositAsync(client, from.Id, 10_000);

        var response = await TransferAsync(client, from.Id, to.Id, 1_000);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Transfers_ExceedingDailyLimit_Return422_WithLimitAndRemaining()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        using var client = await TestUsers.NewSignedInClientAsync(factory);
        var from = await OpenAsync(client);
        var to = await OpenAsync(client);
        await SetLimitsAsync(client, from.Id, 1_000, 2_500);
        await DepositAsync(client, from.Id, 10_000);
        Assert.Equal(HttpStatusCode.OK, (await TransferAsync(client, from.Id, to.Id, 1_000)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await TransferAsync(client, from.Id, to.Id, 1_000)).StatusCode);

        var exceeded = await TransferAsync(client, from.Id, to.Id, 501);
        var exact = await TransferAsync(client, from.Id, to.Id, 500);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, exceeded.StatusCode);
        var error = await ErrorAsync(exceeded);
        Assert.Equal("daily_limit_exceeded", error.GetProperty("code").GetString());
        Assert.Equal(2_500, error.GetProperty("limit").GetInt64());
        Assert.Equal(500, error.GetProperty("remaining").GetInt64());
        Assert.Equal(HttpStatusCode.OK, exact.StatusCode);
        Assert.Equal(0, (await GetLimitsAsync(client, from.Id)).RemainingToday);
    }

    [Fact]
    public async Task Transfer_ExceedingBothLimits_ReportsPerOperation()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        using var client = await TestUsers.NewSignedInClientAsync(factory);
        var from = await OpenAsync(client);
        var to = await OpenAsync(client);
        await SetLimitsAsync(client, from.Id, 1_000, 1_000);
        await DepositAsync(client, from.Id, 10_000);
        Assert.Equal(HttpStatusCode.OK, (await TransferAsync(client, from.Id, to.Id, 1_000)).StatusCode);

        var response = await TransferAsync(client, from.Id, to.Id, 1_001);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("per_operation_limit_exceeded", await ErrorCodeAsync(response));
    }

    [Fact]
    public async Task Limits_ApplyOnlyToOutgoingTransfers()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        using var alice = await TestUsers.NewSignedInClientAsync(factory);
        using var bob = await TestUsers.NewSignedInClientAsync(factory, name: "Bob");
        var from = await OpenAsync(alice);
        var to = await OpenAsync(bob);
        await SetLimitsAsync(alice, from.Id, 1_000, 2_000);
        await SetLimitsAsync(bob, to.Id, 100, 100);

        await DepositAsync(alice, from.Id, 5_000);
        await DepositAsync(bob, to.Id, 5_000);
        var incoming = await TransferAsync(alice, from.Id, to.Id, 1_000);

        Assert.Equal(HttpStatusCode.OK, incoming.StatusCode);
        Assert.Equal(0, (await GetLimitsAsync(bob, to.Id)).SpentToday);
        Assert.Equal(1_000, (await GetLimitsAsync(alice, from.Id)).RemainingToday);
        Assert.Equal(6_000, (await bob.GetFromJsonAsync<AccountDto>($"/accounts/{to.Id}"))!.Balance);
    }

    [Fact]
    public async Task Deposit_AbovePerOperationLimit_Passes_AndDoesNotSpendTheLimit()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        using var client = await TestUsers.NewSignedInClientAsync(factory);
        var account = await OpenAsync(client);
        await SetLimitsAsync(client, account.Id, 100, 100);

        await DepositAsync(client, account.Id, 50_000);

        var limits = await GetLimitsAsync(client, account.Id);
        Assert.Equal(0, limits.SpentToday);
        Assert.Equal(100, limits.RemainingToday);
    }

    [Fact]
    public async Task IdempotentReplay_AfterTheLimitIsExhausted_ReturnsTheStoredResponse()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        using var client = await TestUsers.NewSignedInClientAsync(factory);
        var from = await OpenAsync(client);
        var to = await OpenAsync(client);
        await SetLimitsAsync(client, from.Id, 1_000, 1_000);
        await DepositAsync(client, from.Id, 10_000);
        var original = await TransferAsync(client, from.Id, to.Id, 1_000, key: "limit-replay");
        Assert.Equal(HttpStatusCode.OK, original.StatusCode);
        var stored = await original.Content.ReadFromJsonAsync<TransferDto>();

        var replay = await TransferAsync(client, from.Id, to.Id, 1_000, key: "limit-replay");

        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.True(replay.Headers.Contains("Idempotent-Replayed"));
        Assert.Equal(stored!.Id, (await replay.Content.ReadFromJsonAsync<TransferDto>())!.Id);
        Assert.Equal(1_000, (await GetLimitsAsync(client, from.Id)).SpentToday);
    }

    [Fact]
    public async Task SpentToday_StartsAtUtcMidnight_ByTheClock()
    {
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        await using var factory = await ApiFactory.CreateAsync(postgres, configureServices: services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(clock);
        });
        using var client = await TestUsers.NewSignedInClientAsync(factory);
        var from = await OpenAsync(client);
        var to = await OpenAsync(client);
        await SetLimitsAsync(client, from.Id, 1_000, 3_000);
        await DepositAsync(client, from.Id, 10_000);
        var midnight = new DateTimeOffset(2030, 5, 7, 0, 0, 0, TimeSpan.Zero);

        clock.Set(midnight.AddMilliseconds(-1));
        Assert.Equal(HttpStatusCode.OK, (await TransferAsync(client, from.Id, to.Id, 100)).StatusCode);
        Assert.Equal(100, (await GetLimitsAsync(client, from.Id)).SpentToday);

        clock.Set(midnight);
        Assert.Equal(0, (await GetLimitsAsync(client, from.Id)).SpentToday);
        Assert.Equal(HttpStatusCode.OK, (await TransferAsync(client, from.Id, to.Id, 50)).StatusCode);
        var today = await GetLimitsAsync(client, from.Id);

        Assert.Equal(50, today.SpentToday);
        Assert.Equal(2_950, today.RemainingToday);

        clock.Set(midnight.AddDays(1).AddMilliseconds(-1));
        Assert.Equal(50, (await GetLimitsAsync(client, from.Id)).SpentToday);
        clock.Set(midnight.AddDays(1));
        Assert.Equal(0, (await GetLimitsAsync(client, from.Id)).SpentToday);
    }

    [Fact]
    public async Task ParallelTransfers_NeverSpendMoreThanTheDailyLimit()
    {
        const long daily = 1_000;
        const long amount = daily / 10 + 1;
        await using var factory = await ApiFactory.CreateAsync(postgres);
        using var client = await TestUsers.NewSignedInClientAsync(factory);
        var from = await OpenAsync(client);
        var to = await OpenAsync(client);
        await SetLimitsAsync(client, from.Id, daily, daily);
        await DepositAsync(client, from.Id, 100_000);

        var responses = await ParallelRunner.RunAsync(
            Enumerable.Range(0, 20).Select<int, Func<Task<HttpResponseMessage>>>(_ =>
                () => TransferAsync(client, from.Id, to.Id, amount)),
            limit: 20);

        var fits = (int)(daily / amount);
        Assert.Equal(fits, responses.Count(r => r.StatusCode == HttpStatusCode.OK));
        foreach (var rejected in responses.Where(r => r.StatusCode != HttpStatusCode.OK))
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, rejected.StatusCode);
            Assert.Equal("daily_limit_exceeded", await ErrorCodeAsync(rejected));
        }

        var limits = await GetLimitsAsync(client, from.Id);
        Assert.Equal(fits * amount, limits.SpentToday);
        Assert.True(limits.SpentToday <= daily);
    }
}
