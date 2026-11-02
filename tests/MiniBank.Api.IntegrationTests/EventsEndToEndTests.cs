using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using MiniBank.Application;
using MiniBank.Messaging;
using MiniBank.Notifications;
using MiniBank.TestSupport;
using NotificationsHandler = MiniBank.Notifications.EventHandler;

namespace MiniBank.Api.IntegrationTests;

[Collection("postgres")]
public class EventsEndToEndTests(PostgresFixture postgres, RabbitMqFixture rabbit) : IClassFixture<RabbitMqFixture>
{
    private sealed class Factory(string connectionString) : IDbContextFactory<NotificationsDbContext>
    {
        public NotificationsDbContext CreateDbContext() => new(NotificationsDbContext.CreateOptions(connectionString));
    }

    private static Dictionary<string, string?> BrokerSettings(RabbitMqOptions broker) => new()
    {
        ["Events:Enabled"] = "true",
        ["Events:PollInterval"] = "00:00:00.200",
        ["RabbitMq:Host"] = broker.Host,
        ["RabbitMq:Port"] = broker.Port.ToString(),
        ["RabbitMq:User"] = broker.User,
        ["RabbitMq:Password"] = broker.Password,
    };

    private static async Task<AccountDto> CreateAccountAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/accounts", new { currency = "RUB" });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<AccountDto>())!;
    }

    [Fact]
    public async Task Transfer_ThroughApi_ReachesConsumer()
    {
        await rabbit.ResetAsync();
        var consumerDbString = await postgres.CreateDatabaseAsync();
        var consumerDb = new Factory(consumerDbString);
        await using (var context = consumerDb.CreateDbContext())
            await context.Database.MigrateAsync();

        await using var factory = await ApiFactory.CreateAsync(postgres, settings: BrokerSettings(rabbit.Options));
        using var consumer = new EventConsumer(
            rabbit.Options, new NotificationsHandler(consumerDb, NullLogger<NotificationsHandler>.Instance), NullLogger<EventConsumer>.Instance);
        await consumer.StartAsync(CancellationToken.None);
        try
        {
            using var alice = await TestUsers.NewSignedInClientAsync(factory, name: "Alice");
            using var bob = await TestUsers.NewSignedInClientAsync(factory, name: "Bob");
            var from = await CreateAccountAsync(alice);
            var to = await CreateAccountAsync(bob);

            (await alice.PostWithKeyAsync($"/accounts/{from.Id}/deposit", new { amount = 10_000, currency = "RUB" }))
                .EnsureSuccessStatusCode();
            (await alice.PostWithKeyAsync("/transfers", new { fromAccountId = from.Id, toAccountId = to.Id, amount = 2_500, currency = "RUB" }))
                .EnsureSuccessStatusCode();

            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
            int processed = 0;
            while (DateTime.UtcNow < deadline && processed < 2)
            {
                await Task.Delay(200);
                await using var context = consumerDb.CreateDbContext();
                processed = await context.ProcessedEvents.CountAsync();
            }

            await using var check = consumerDb.CreateDbContext();
            Assert.Equal(2, processed);
            var received = await check.ReceivedEvents.ToListAsync();
            Assert.Equal(3, received.Count); // пополнение: владелец; перевод: оба владельца
            Assert.Equal(2, received.Select(e => e.OwnerId).Distinct().Count());
            Assert.Equal(0u, await rabbit.MessageCountAsync(RabbitMqTopology.DeadQueue));
        }
        finally
        {
            await consumer.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task ApiHealth_IgnoresBroker()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres, settings: BrokerSettings(RabbitMqFixture.Unreachable));
        using var client = await TestUsers.NewSignedInClientAsync(factory);
        var account = await CreateAccountAsync(client);

        var health = await client.GetAsync("/health");
        var deposit = await client.PostWithKeyAsync($"/accounts/{account.Id}/deposit", new { amount = 1_000, currency = "RUB" });

        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        Assert.Equal(HttpStatusCode.OK, deposit.StatusCode);
    }
}
