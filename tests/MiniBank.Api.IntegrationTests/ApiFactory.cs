using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MiniBank.Application.Abstractions;
using MiniBank.TestSupport;

namespace MiniBank.Api.IntegrationTests;

/// <summary>Поднимает API поверх чистой базы с применёнными миграциями (по умолчанию в окружении Development).</summary>
public sealed class ApiFactory : WebApplicationFactory<Program>
{
    public const string JwtKey = "integration-tests-signing-key-0123456789";

    private const string UnreachableConnectionString =
        "Host=127.0.0.1;Port=1;Database=x;Username=x;Password=x;Timeout=2";

    private readonly string _connectionString;
    private readonly bool _migrate;
    private readonly string _environment;
    private readonly IReadOnlyDictionary<string, string?>? _settings;
    private readonly Action<IServiceCollection>? _configureServices;

    private ApiFactory(
        string connectionString,
        bool migrate,
        string environment,
        IReadOnlyDictionary<string, string?>? settings,
        Action<IServiceCollection>? configureServices)
    {
        _connectionString = connectionString;
        _migrate = migrate;
        _environment = environment;
        _settings = settings;
        _configureServices = configureServices;
    }

    public string ConnectionString => _connectionString;

    /// <summary>Письма, которые API «отправил» за время жизни фабрики.</summary>
    public FakeEmailSender Email { get; } = new();

    public static async Task<ApiFactory> CreateAsync(
        PostgresFixture postgres,
        string environment = "Development",
        IReadOnlyDictionary<string, string?>? settings = null,
        Action<IServiceCollection>? configureServices = null) =>
        new(await postgres.CreateDatabaseAsync(), migrate: true, environment, settings, configureServices);

    /// <summary>API в окружении Production, база которого недоступна (миграции выключены, иначе старт упадёт).</summary>
    public static ApiFactory CreateUnreachable() =>
        new(UnreachableConnectionString, migrate: false, "Production", settings: null, configureServices: null);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(_environment);
        builder.UseSetting("ConnectionStrings:MiniBank", _connectionString);
        builder.UseSetting("Database:MigrateOnStartup", _migrate ? "true" : "false");

        // Обязательные настройки аутентификации; лимит запросов высокий, чтобы не мешать остальным тестам.
        builder.UseSetting("Jwt:Key", JwtKey);
        builder.UseSetting("Web:BaseUrl", "https://app.test");
        builder.UseSetting("Auth:RateLimitPerMinute", "1000");
        // Публикатор по умолчанию выключен: события пишутся в outbox, но брокер тестам API не нужен.
        builder.UseSetting("Events:Enabled", "false");

        foreach (var (key, value) in _settings ?? new Dictionary<string, string?>())
            builder.UseSetting(key, value);

        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IEmailSender>();
            services.AddSingleton<IEmailSender>(Email);
            _configureServices?.Invoke(services);
        });
    }
}
