using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using MiniBank.TestSupport;

namespace MiniBank.Api.IntegrationTests;

/// <summary>Поднимает API в окружении Development поверх чистой базы с применёнными миграциями.</summary>
public sealed class ApiFactory : WebApplicationFactory<Program>
{
    private readonly string _connectionString;

    private ApiFactory(string connectionString) => _connectionString = connectionString;

    public static async Task<ApiFactory> CreateAsync(PostgresFixture postgres) =>
        new(await postgres.CreateDatabaseAsync());

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.UseSetting("ConnectionStrings:MiniBank", _connectionString);
        builder.UseSetting("Database:MigrateOnStartup", "true");
    }
}
