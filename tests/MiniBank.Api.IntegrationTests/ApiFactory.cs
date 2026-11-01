using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using MiniBank.TestSupport;

namespace MiniBank.Api.IntegrationTests;

/// <summary>Поднимает API поверх чистой базы с применёнными миграциями (по умолчанию в окружении Development).</summary>
public sealed class ApiFactory : WebApplicationFactory<Program>
{
    private const string UnreachableConnectionString =
        "Host=127.0.0.1;Port=1;Database=x;Username=x;Password=x;Timeout=2";

    private readonly string _connectionString;
    private readonly bool _migrate;
    private readonly string _environment;
    private readonly IReadOnlyDictionary<string, string?>? _settings;

    private ApiFactory(
        string connectionString, bool migrate, string environment, IReadOnlyDictionary<string, string?>? settings)
    {
        _connectionString = connectionString;
        _migrate = migrate;
        _environment = environment;
        _settings = settings;
    }

    public string ConnectionString => _connectionString;

    public static async Task<ApiFactory> CreateAsync(
        PostgresFixture postgres,
        string environment = "Development",
        IReadOnlyDictionary<string, string?>? settings = null) =>
        new(await postgres.CreateDatabaseAsync(), migrate: true, environment, settings);

    /// <summary>API в окружении Production, база которого недоступна (миграции выключены, иначе старт упадёт).</summary>
    public static ApiFactory CreateUnreachable() =>
        new(UnreachableConnectionString, migrate: false, "Production", settings: null);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(_environment);
        builder.UseSetting("ConnectionStrings:MiniBank", _connectionString);
        builder.UseSetting("Database:MigrateOnStartup", _migrate ? "true" : "false");

        foreach (var (key, value) in _settings ?? new Dictionary<string, string?>())
            builder.UseSetting(key, value);
    }
}
