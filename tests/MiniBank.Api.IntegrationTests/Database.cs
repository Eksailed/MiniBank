using MiniBank.Domain;
using Npgsql;

namespace MiniBank.Api.IntegrationTests;

internal static class Database
{
    /// <summary>Баланс кассы валюты: через API системные счета недоступны, поэтому читаем напрямую из базы.</summary>
    public static async Task<long> CashBalanceAsync(string connectionString, string currency = "RUB")
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT balance FROM accounts WHERE id = @id", connection);
        command.Parameters.AddWithValue("id", SystemAccounts.CashId(currency));
        return (long)(await command.ExecuteScalarAsync())!;
    }
}
