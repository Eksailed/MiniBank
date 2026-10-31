using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using MiniBank.Application.Abstractions;
using MiniBank.Domain;
using MiniBank.TestSupport;

namespace MiniBank.Api.IntegrationTests;

[Collection("postgres")]
public class ErrorLoggingTests(PostgresFixture postgres)
{
    private sealed record LogRecord(LogLevel Level, string Category, string Message);

    private sealed class CollectingLoggerProvider : ILoggerProvider
    {
        public ConcurrentQueue<LogRecord> Records { get; } = new();

        public ILogger CreateLogger(string categoryName) => new CollectingLogger(categoryName, Records);

        public void Dispose()
        {
        }

        private sealed class CollectingLogger(string category, ConcurrentQueue<LogRecord> records) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter) =>
                records.Enqueue(new LogRecord(logLevel, category, formatter(state, exception)));
        }
    }

    private sealed class ThrowingAccountRepository : IAccountRepository
    {
        public Task<Account?> FindAsync(Guid id, CancellationToken ct) =>
            throw new InvalidOperationException("secret db detail");

        public Task<IReadOnlyDictionary<Guid, Account>> FindForUpdateAsync(
            IReadOnlyCollection<Guid> ids, CancellationToken ct) =>
            throw new InvalidOperationException("secret db detail");

        public void Add(Account account) => throw new InvalidOperationException("secret db detail");
    }

    private static HttpClient CreateClient(
        ApiFactory factory, CollectingLoggerProvider logs, bool throwingRepository, out IDisposable owner)
    {
        var configured = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureLogging(logging => logging.AddProvider(logs));
            if (throwingRepository)
                builder.ConfigureServices(services =>
                {
                    services.RemoveAll<IAccountRepository>();
                    services.AddScoped<IAccountRepository, ThrowingAccountRepository>();
                });
        });
        owner = configured;
        var client = configured.CreateClient();
        // Запуск хоста применяет миграции и пишет служебные записи (EF логирует Error при проверке пустой БД):
        // нас интересуют только записи, появившиеся при обработке запросов.
        logs.Records.Clear();
        return client;
    }

    [Fact]
    public async Task BusinessErrors_AreNotLoggedAsError()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        var logs = new CollectingLoggerProvider();
        using var client = CreateClient(factory, logs, throwingRepository: false, out var owner);
        using var _ = owner;

        var notFound = await client.GetAsync($"/accounts/{Guid.NewGuid()}");
        var invalid = await client.PostAsJsonAsync("/accounts", new { owner = "", currency = "RUB" });

        Assert.Equal(HttpStatusCode.NotFound, notFound.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.DoesNotContain(logs.Records, r => r.Level >= LogLevel.Error);
    }

    [Fact]
    public async Task UnhandledException_Returns500WithoutDetails_AndIsLoggedOnce()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        var logs = new CollectingLoggerProvider();
        using var client = CreateClient(factory, logs, throwingRepository: true, out var owner);
        using var _ = owner;

        var response = await client.GetAsync($"/accounts/{Guid.NewGuid()}");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("An unexpected error occurred.", body);
        Assert.DoesNotContain("secret db detail", body);
        Assert.Single(logs.Records, r => r.Level >= LogLevel.Error);
    }
}
