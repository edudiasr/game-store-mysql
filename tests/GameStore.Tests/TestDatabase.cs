using System.Data;
using GameStore.Api.Db;
using MySqlConnector;

namespace GameStore.Tests;

/// <summary>
/// Um banco MySQL novo pra cada teste: cria, roda as migrations e o seed, e apaga no fim.
/// Aponta pro servidor em GAMESTORE_TEST_MYSQL (no CI é um container de serviço do GitHub Actions).
/// </summary>
public sealed class TestDatabase : IAsyncDisposable
{
    public static string ServerConnection =>
        Environment.GetEnvironmentVariable("GAMESTORE_TEST_MYSQL")
        ?? "Server=127.0.0.1;Port=3306;User ID=gamestore;Password=gamestore_dev";

    public string Name { get; } = "gamestore_test_" + Guid.NewGuid().ToString("N")[..12];

    public string ConnectionString =>
        new MySqlConnectionStringBuilder(ServerConnection) { Database = Name }.ConnectionString;

    private static string DbFolder => Path.Combine(AppContext.BaseDirectory, "db");

    public static async Task<TestDatabase> CreateAsync()
    {
        var db = new TestDatabase();
        await using (var server = new MySqlConnection(ServerConnection))
        {
            await server.OpenAsync();
            await using var cmd = new MySqlCommand($"CREATE DATABASE `{db.Name}`", server);
            await cmd.ExecuteNonQueryAsync();
        }
        await Migrator.MigrateAsync(db.ConnectionString, Path.Combine(DbFolder, "migrations"));
        await using var conn = await db.OpenAsync();
        await Migrator.RunScriptAsync(conn, await File.ReadAllTextAsync(Path.Combine(DbFolder, "seed.sql")));
        return db;
    }

    public async Task<MySqlConnection> OpenAsync()
    {
        var conn = new MySqlConnection(ConnectionString);
        await conn.OpenAsync();
        return conn;
    }

    public async Task<T> ScalarAsync<T>(string sql)
    {
        await using var conn = await OpenAsync();
        await using var cmd = new MySqlCommand(sql, conn);
        return (T)Convert.ChangeType((await cmd.ExecuteScalarAsync())!, typeof(T));
    }

    public async Task ExecAsync(string sql)
    {
        await using var conn = await OpenAsync();
        await using var cmd = new MySqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<long> PlaceOrderAsync(string steamId, string itemsJson, string? coupon = null)
    {
        await using var conn = await OpenAsync();
        await using var cmd = new MySqlCommand("sp_place_order", conn) { CommandType = CommandType.StoredProcedure };
        cmd.Parameters.AddWithValue("p_steam_id", steamId);
        cmd.Parameters.AddWithValue("p_items", itemsJson);
        cmd.Parameters.AddWithValue("p_coupon", coupon);
        cmd.Parameters.Add(new MySqlParameter("p_order_id", MySqlDbType.Int64) { Direction = ParameterDirection.Output });
        await cmd.ExecuteNonQueryAsync();
        return Convert.ToInt64(cmd.Parameters["p_order_id"].Value);
    }

    public async Task<string> ConfirmPaymentAsync(long orderId, string reference, int amountCents, string provider = "pix")
    {
        await using var conn = await OpenAsync();
        await using var cmd = new MySqlCommand("sp_confirm_payment", conn) { CommandType = CommandType.StoredProcedure };
        cmd.Parameters.AddWithValue("p_order_id", orderId);
        cmd.Parameters.AddWithValue("p_provider", provider);
        cmd.Parameters.AddWithValue("p_ref", reference);
        cmd.Parameters.AddWithValue("p_amount", amountCents);
        cmd.Parameters.Add(new MySqlParameter("p_result", MySqlDbType.VarChar, 16) { Direction = ParameterDirection.Output });
        await cmd.ExecuteNonQueryAsync();
        return (string)cmd.Parameters["p_result"].Value!;
    }

    public async Task<int> SpendAsync(string steamId, int amount)
    {
        await using var conn = await OpenAsync();
        await using var cmd = new MySqlCommand("sp_spend_coins", conn) { CommandType = CommandType.StoredProcedure };
        cmd.Parameters.AddWithValue("p_steam_id", steamId);
        cmd.Parameters.AddWithValue("p_amount", amount);
        cmd.Parameters.Add(new MySqlParameter("p_balance", MySqlDbType.Int32) { Direction = ParameterDirection.Output });
        await cmd.ExecuteNonQueryAsync();
        return Convert.ToInt32(cmd.Parameters["p_balance"].Value);
    }

    public async ValueTask DisposeAsync()
    {
        MySqlConnection.ClearAllPools();
        await using var server = new MySqlConnection(ServerConnection);
        await server.OpenAsync();
        await using var cmd = new MySqlCommand($"DROP DATABASE IF EXISTS `{Name}`", server);
        await cmd.ExecuteNonQueryAsync();
    }
}
