using System.Net;
using MiniBank.TestSupport;

namespace MiniBank.Api.IntegrationTests;

[Collection("postgres")]
public class HealthAndSwaggerTests(PostgresFixture postgres)
{
    private const string SwaggerJson = "/swagger/v1/swagger.json";

    [Fact]
    public async Task Health_WithLiveDatabase_Returns200Healthy()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Health_WithUnreachableDatabase_Returns503()
    {
        await using var factory = ApiFactory.CreateUnreachable();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task Swagger_InProductionWithoutFlag_Returns404()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres, "Production");
        using var client = await TestUsers.NewSignedInClientAsync(factory);

        var response = await client.GetAsync(SwaggerJson);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Swagger_InProductionWithFlag_Returns200AndOmitsHealth()
    {
        await using var factory = await ApiFactory.CreateAsync(
            postgres, "Production", new Dictionary<string, string?> { ["Swagger:Enabled"] = "true" });
        using var client = factory.CreateClient();

        var response = await client.GetAsync(SwaggerJson);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"/accounts\"", body);
        Assert.DoesNotContain("\"/health\"", body);
    }

    [Fact]
    public async Task Swagger_InDevelopment_Returns200()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        using var client = factory.CreateClient();

        var response = await client.GetAsync(SwaggerJson);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
