using System.Text.RegularExpressions;
using MySqlConnector;

namespace GameStore.Api.Db;

/// <summary>
/// Aplica os arquivos db/migrations/V###__nome.sql em ordem, uma vez cada.
/// O cliente do MySQL não entende DELIMITER, então os arquivos separam os comandos
/// com uma linha "-- @@" e cada pedaço vai sozinho pro servidor.
/// </summary>
public static partial class Migrator
{
    [GeneratedRegex(@"^\s*--\s*@@\s*$", RegexOptions.Multiline)]
    private static partial Regex Separator();

    public static async Task<IReadOnlyList<string>> MigrateAsync(string connectionString, string folder, CancellationToken ct = default)
    {
        await using var conn = new MySqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await ExecuteAsync(conn, """
            CREATE TABLE IF NOT EXISTS schema_migrations (
                version    VARCHAR(100) PRIMARY KEY,
                applied_at DATETIME(3) NOT NULL DEFAULT CURRENT_TIMESTAMP(3)
            )
            """, ct);

        var applied = new HashSet<string>();
        await using (var cmd = new MySqlCommand("SELECT version FROM schema_migrations", conn))
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct))
                applied.Add(reader.GetString(0));

        var done = new List<string>();
        foreach (var file in Directory.GetFiles(folder, "V*.sql").Order(StringComparer.Ordinal))
        {
            var version = Path.GetFileNameWithoutExtension(file);
            if (applied.Contains(version)) continue;

            // DDL no MySQL faz commit implícito, então não dá pra envolver a migration numa transação.
            // Se uma falhar no meio, a versão não é gravada e o erro aparece na hora.
            await RunScriptAsync(conn, await File.ReadAllTextAsync(file, ct), ct);
            await ExecuteAsync(conn, $"INSERT INTO schema_migrations (version) VALUES ('{version}')", ct);
            done.Add(version);
        }
        return done;
    }

    public static async Task RunScriptAsync(MySqlConnection conn, string script, CancellationToken ct = default)
    {
        foreach (var statement in Separator().Split(script))
            if (!string.IsNullOrWhiteSpace(StripComments(statement)))
                await ExecuteAsync(conn, statement, ct);
    }

    private static string StripComments(string sql) =>
        string.Join('\n', sql.Split('\n').Where(l => !l.TrimStart().StartsWith("--")));

    private static async Task ExecuteAsync(MySqlConnection conn, string sql, CancellationToken ct)
    {
        await using var cmd = new MySqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
