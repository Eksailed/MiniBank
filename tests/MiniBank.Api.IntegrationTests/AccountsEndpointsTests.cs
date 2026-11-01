using System.Net;
using System.Net.Http.Json;
using System.Text;
using MiniBank.Application;
using MiniBank.TestSupport;

namespace MiniBank.Api.IntegrationTests;

[Collection("postgres")]
public class AccountsEndpointsTests(PostgresFixture postgres)
{
    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    [Fact]
    public async Task PostAccounts_Returns201WithLocationAndZeroBalance()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        using var client = await TestUsers.NewSignedInClientAsync(factory);

        var response = await client.PostAsJsonAsync("/accounts", new { currency = "RUB" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var account = await response.Content.ReadFromJsonAsync<AccountDto>();
        Assert.NotNull(account);
        Assert.Equal(0, account.Balance);
        Assert.Equal("Alice", account.Owner);
        Assert.Equal($"/accounts/{account.Id}", response.Headers.Location?.AbsolutePath);
    }

    [Fact]
    public async Task PostAccounts_LowercaseCurrency_IsNormalized()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        using var client = await TestUsers.NewSignedInClientAsync(factory);

        var response = await client.PostAsJsonAsync("/accounts", new { currency = "rub" });

        var account = await response.Content.ReadFromJsonAsync<AccountDto>();
        Assert.Equal("RUB", account!.Currency);
    }

    [Theory]
    [InlineData("JPY")]
    [InlineData("RU")]
    [InlineData("")]
    public async Task PostAccounts_BadCurrency_Returns400ProblemDetails(string currency)
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        using var client = await TestUsers.NewSignedInClientAsync(factory);

        var response = await client.PostAsJsonAsync("/accounts", new { currency });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task PostAccounts_MalformedJson_Returns400()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        using var client = await TestUsers.NewSignedInClientAsync(factory);

        var response = await client.PostAsync("/accounts", Json("{not json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task PostAccounts_MissingFields_Returns400()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        using var client = await TestUsers.NewSignedInClientAsync(factory);

        var response = await client.PostAsync("/accounts", Json("{}"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task GetAccount_Returns200AndUnknownReturns404()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        using var client = await TestUsers.NewSignedInClientAsync(factory);
        var created = await (await client.PostAsJsonAsync("/accounts", new { currency = "RUB" }))
            .Content.ReadFromJsonAsync<AccountDto>();

        var found = await client.GetAsync($"/accounts/{created!.Id}");
        var missing = await client.GetAsync($"/accounts/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.OK, found.StatusCode);
        Assert.Equal(created.Id, (await found.Content.ReadFromJsonAsync<AccountDto>())!.Id);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal("application/problem+json", missing.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Swagger_JsonIsServed()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/swagger/v1/swagger.json");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
