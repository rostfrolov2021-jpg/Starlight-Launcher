using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace Starlight.Server;

// One service instance owns this snapshot. PostgreSQL keeps it durable on hosts with ephemeral disks.
// Revision checks reject conflicting writers instead of silently losing accounts or friend requests.
public sealed class AccountStorage : IAsyncDisposable
{
    private readonly string file;
    private readonly NpgsqlDataSource? database;
    private long revision;
    public AccountStorage(IConfiguration config)
    {
        file = Path.GetFullPath(config["STARLIGHT_DATA"] ?? Path.Combine(AppContext.BaseDirectory, "data", "users.json"));
        var value = config["STARLIGHT_DATABASE_URL"];
        if (string.IsNullOrWhiteSpace(value))
        {
            if (config["STARLIGHT_REQUIRE_DATABASE"] == "true") throw new InvalidOperationException("STARLIGHT_DATABASE_URL is required on this host. Local fallback is disabled.");
            Directory.CreateDirectory(Path.GetDirectoryName(file)!); return;
        }
        NpgsqlConnectionStringBuilder connection;
        try
        {
            if (value.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase) || value.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase))
            {
                var uri = new Uri(value); var credentials = uri.UserInfo.Split(':', 2);
                connection = new() { Host = uri.Host, Port = uri.Port > 0 ? uri.Port : 5432,
                    Database = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/')),
                    Username = Uri.UnescapeDataString(credentials[0]), Password = credentials.Length > 1 ? Uri.UnescapeDataString(credentials[1]) : "" };
            }
            else connection = new(value);
            var local = connection.Host is "localhost" or "127.0.0.1" or "::1";
            connection.SslMode = local ? SslMode.Disable : SslMode.VerifyFull;
            connection.Timeout = 15; connection.CommandTimeout = 30; connection.MaxPoolSize = 5;
            connection.IncludeErrorDetail = false;
            database = NpgsqlDataSource.Create(connection.ConnectionString);
        }
        catch { throw new InvalidOperationException("Invalid STARLIGHT_DATABASE_URL. Check the private database connection setting."); }
    }
    public async Task<Dictionary<string, User>> LoadAsync(CancellationToken ct = default)
    {
        if (database == null) return File.Exists(file) ? JsonSerializer.Deserialize<Dictionary<string, User>>(await File.ReadAllTextAsync(file, ct)) ?? [] : [];
        await using (var setup = database.CreateCommand("CREATE TABLE IF NOT EXISTS starlight_accounts (id integer PRIMARY KEY CHECK (id = 1), revision bigint NOT NULL, data jsonb NOT NULL); INSERT INTO starlight_accounts(id, revision, data) VALUES (1, 0, '{}'::jsonb) ON CONFLICT (id) DO NOTHING;"))
            await setup.ExecuteNonQueryAsync(ct);
        await using var query = database.CreateCommand("SELECT revision, data::text FROM starlight_accounts WHERE id = 1");
        await using var reader = await query.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) throw new InvalidDataException("Account storage is missing.");
        revision = reader.GetInt64(0);
        return JsonSerializer.Deserialize<Dictionary<string, User>>(reader.GetString(1)) ?? throw new InvalidDataException("Account storage is invalid.");
    }
    public async Task SaveAsync(Dictionary<string, User> users)
    {
        var json = JsonSerializer.Serialize(users);
        if (database == null) { await File.WriteAllTextAsync(file + ".tmp", json); File.Move(file + ".tmp", file, true); return; }
        await using var command = database.CreateCommand("UPDATE starlight_accounts SET data = $1, revision = revision + 1 WHERE id = 1 AND revision = $2");
        command.Parameters.AddWithValue(NpgsqlDbType.Jsonb, json); command.Parameters.AddWithValue(revision);
        if (await command.ExecuteNonQueryAsync() != 1) throw new AccountStorageConflictException();
        revision++;
    }
    public async ValueTask DisposeAsync() { if (database != null) await database.DisposeAsync(); }
}

public sealed class AccountStorageConflictException : Exception;
