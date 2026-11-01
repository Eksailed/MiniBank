using Microsoft.EntityFrameworkCore;
using MiniBank.Domain;
using MiniBank.Infrastructure.Persistence;
using MiniBank.TestSupport;
using Npgsql;

namespace MiniBank.Infrastructure.Tests;

[Collection("postgres")]
public class SchemaTests(PostgresFixture postgres)
{
    private static readonly DateTimeOffset Now = new(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);

    private async Task<(string ConnectionString, MiniBankDbContext Context)> MigratedDatabaseAsync()
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        var context = NewContext(connectionString);
        await context.Database.MigrateAsync();
        return (connectionString, context);
    }

    private static MiniBankDbContext NewContext(string connectionString) =>
        new(new DbContextOptionsBuilder<MiniBankDbContext>()
            .UseNpgsql(connectionString)
            .UseSnakeCaseNamingConvention()
            .Options);

    private static async Task<long> ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return await command.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task Migrate_SeedsOneCashAccountPerSupportedCurrency()
    {
        var (_, context) = await MigratedDatabaseAsync();
        await using var _ = context;

        var system = await context.Accounts.Where(a => a.IsSystem).ToListAsync();

        Assert.Equal(Currencies.Supported.Count, system.Count);
        foreach (var currency in Currencies.Supported)
        {
            var cash = Assert.Single(system, a => a.Currency == currency);
            Assert.Equal(SystemAccounts.CashId(currency), cash.Id);
            Assert.Equal(0, cash.Balance);
        }
    }

    [Fact]
    public async Task CheckConstraint_RejectsNegativeBalanceForRegularAccount()
    {
        var (connectionString, context) = await MigratedDatabaseAsync();
        await using var _ = context;

        var ex = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(connectionString,
            "INSERT INTO accounts (id, owner, currency, balance, version, status, is_system, created_at) " +
            $"VALUES ('{Guid.NewGuid()}', 'x', 'RUB', -1, 0, 0, false, now())"));

        Assert.Equal("23514", ex.SqlState);
    }

    [Fact]
    public async Task CheckConstraint_AllowsNegativeBalanceForSystemAccount()
    {
        var (connectionString, context) = await MigratedDatabaseAsync();
        await using var _ = context;

        var updated = await ExecuteAsync(connectionString,
            $"UPDATE accounts SET balance = -5 WHERE id = '{SystemAccounts.CashId("RUB")}'");

        Assert.Equal(1, updated);
    }

    [Fact]
    public async Task LedgerTrigger_RejectsUpdateAndDelete()
    {
        var (connectionString, context) = await MigratedDatabaseAsync();
        await using var _ = context;
        var from = Account.Open("a", "RUB", Now);
        var to = Account.Open("b", "RUB", Now);
        from.Credit(Guid.NewGuid(), Money.Of(100, "RUB"), Now);
        context.Accounts.AddRange(from, to);
        context.Transfers.Add(Transfer.Execute(from, to, Money.Of(10, "RUB"), Now));
        await context.SaveChangesAsync();

        await Assert.ThrowsAsync<PostgresException>(() =>
            ExecuteAsync(connectionString, "UPDATE ledger_entries SET amount = 1"));
        await Assert.ThrowsAsync<PostgresException>(() =>
            ExecuteAsync(connectionString, "DELETE FROM ledger_entries"));
    }

    [Fact]
    public async Task LedgerTrigger_RejectsTruncate()
    {
        var (connectionString, context) = await MigratedDatabaseAsync();
        await using var _ = context;
        var from = Account.Open("a", "RUB", Now);
        var to = Account.Open("b", "RUB", Now);
        from.Credit(Guid.NewGuid(), Money.Of(100, "RUB"), Now);
        context.Accounts.AddRange(from, to);
        context.Transfers.Add(Transfer.Execute(from, to, Money.Of(10, "RUB"), Now));
        await context.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<PostgresException>(() =>
            ExecuteAsync(connectionString, "TRUNCATE ledger_entries"));

        Assert.Equal("23001", ex.SqlState);
        Assert.Equal(2, await context.LedgerEntries.CountAsync());
    }

    [Fact]
    public async Task RoundTrip_SavesTransferWithEntries()
    {
        var (connectionString, context) = await MigratedDatabaseAsync();
        var from = Account.Open("a", "RUB", Now);
        var to = Account.Open("b", "RUB", Now);
        from.Credit(Guid.NewGuid(), Money.Of(1000, "RUB"), Now);
        context.Accounts.AddRange(from, to);
        var transfer = Transfer.Execute(from, to, Money.Of(300, "RUB"), Now);
        context.Transfers.Add(transfer);
        await context.SaveChangesAsync();
        await context.DisposeAsync();

        await using var fresh = NewContext(connectionString);
        var loaded = await fresh.Transfers.Include(t => t.Entries).SingleAsync(t => t.Id == transfer.Id);
        var loadedFrom = await fresh.Accounts.SingleAsync(a => a.Id == from.Id);

        Assert.Equal(2, loaded.Entries.Count);
        Assert.Equal(0, loaded.Entries.Sum(e => e.Amount));
        Assert.Equal(300, loaded.Amount);
        Assert.Equal(700, loadedFrom.Balance);
        Assert.Equal(from.Version, loadedFrom.Version);
    }

    [Fact]
    public async Task Users_DuplicateEmail_ViolatesUniqueIndex()
    {
        var (_, context) = await MigratedDatabaseAsync();
        await using var _ = context;
        context.Users.Add(User.Register("a@b.c", "hash", "A", Now));
        await context.SaveChangesAsync();

        context.Users.Add(User.Register("a@b.c", "hash", "B", Now));
        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());

        var pg = Assert.IsType<PostgresException>(ex.InnerException);
        Assert.Equal("23505", pg.SqlState);
        Assert.Equal("ix_users_email", pg.ConstraintName);
    }

    [Fact]
    public async Task EmailConfirmationTokens_DuplicateHash_ViolatesUniqueIndex()
    {
        var (_, context) = await MigratedDatabaseAsync();
        await using var _ = context;
        var user = User.Register("a@b.c", "hash", "A", Now);
        context.Users.Add(user);
        context.EmailConfirmationTokens.Add(EmailConfirmationToken.Create(user.Id, "same", Now));
        await context.SaveChangesAsync();

        context.EmailConfirmationTokens.Add(EmailConfirmationToken.Create(user.Id, "same", Now));
        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());

        var pg = Assert.IsType<PostgresException>(ex.InnerException);
        Assert.Equal("23505", pg.SqlState);
    }

    [Fact]
    public async Task DeletingUser_CascadesToConfirmationTokens()
    {
        var (connectionString, context) = await MigratedDatabaseAsync();
        await using var _ = context;
        var user = User.Register("a@b.c", "hash", "A", Now);
        context.Users.Add(user);
        context.EmailConfirmationTokens.Add(EmailConfirmationToken.Create(user.Id, "h1", Now));
        context.EmailConfirmationTokens.Add(EmailConfirmationToken.Create(user.Id, "h2", Now));
        await context.SaveChangesAsync();

        await ExecuteAsync(connectionString, $"DELETE FROM users WHERE id = '{user.Id}'");

        Assert.Equal(0, await context.EmailConfirmationTokens.CountAsync());
    }

    [Fact]
    public async Task Users_RoundTrip_PreservesFields()
    {
        var (connectionString, context) = await MigratedDatabaseAsync();
        var user = User.Register("A@b.c", "hash", " Alice ", Now);
        user.ConfirmEmail(Now.AddMinutes(1));
        context.Users.Add(user);
        await context.SaveChangesAsync();
        await context.DisposeAsync();

        await using var fresh = NewContext(connectionString);
        var loaded = await fresh.Users.SingleAsync(u => u.Id == user.Id);

        Assert.Equal("a@b.c", loaded.Email);
        Assert.Equal("Alice", loaded.DisplayName);
        Assert.Equal("hash", loaded.PasswordHash);
        Assert.Equal(Now.AddMinutes(1), loaded.EmailConfirmedAt);
        Assert.Equal(Now, loaded.CreatedAt);
    }
}
