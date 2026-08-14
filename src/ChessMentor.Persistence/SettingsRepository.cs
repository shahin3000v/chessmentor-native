using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace ChessMentor.Persistence;

public sealed class SettingsRepository(AppDatabase database, JsonSerializerOptions? serializerOptions = null)
{
    private const string SettingsKey = "application";
    private readonly JsonSerializerOptions _json = serializerOptions ?? new JsonSerializerOptions(JsonSerializerDefaults.Web);

    public event Action<AppSettings>? Updated;

    public Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default) =>
        database.ExecuteAsync(Load, cancellationToken);

    public async Task<AppSettings> ReloadAsync(CancellationToken cancellationToken = default)
    {
        var settings = await LoadAsync(cancellationToken).ConfigureAwait(true);
        Updated?.Invoke(settings);
        return settings;
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        await database.ExecuteAsync(
            connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = """
                    INSERT INTO settings(key, json_value, updated_utc)
                    VALUES($key, $json, $utc)
                    ON CONFLICT(key) DO UPDATE SET
                        json_value = excluded.json_value,
                        updated_utc = excluded.updated_utc;
                    """;
                command.Parameters.AddWithValue("$key", SettingsKey);
                command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(settings, _json));
                command.Parameters.AddWithValue("$utc", DateTimeOffset.UtcNow.ToString("O"));
                command.ExecuteNonQuery();
                return 0;
            },
            cancellationToken).ConfigureAwait(true);
        Updated?.Invoke(settings);
    }

    public async Task<AppSettings> UpdateAsync(
        Func<AppSettings, AppSettings> update,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        var next = await database.ExecuteAsync(
            connection =>
            {
                var next = update(Load(connection));
                using var command = connection.CreateCommand();
                command.CommandText = """
                    INSERT INTO settings(key, json_value, updated_utc)
                    VALUES($key, $json, $utc)
                    ON CONFLICT(key) DO UPDATE SET
                        json_value = excluded.json_value,
                        updated_utc = excluded.updated_utc;
                    """;
                command.Parameters.AddWithValue("$key", SettingsKey);
                command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(next, _json));
                command.Parameters.AddWithValue("$utc", DateTimeOffset.UtcNow.ToString("O"));
                command.ExecuteNonQuery();
                return next;
            },
            cancellationToken).ConfigureAwait(true);
        Updated?.Invoke(next);
        return next;
    }

    private AppSettings Load(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT json_value FROM settings WHERE key = $key;";
        command.Parameters.AddWithValue("$key", SettingsKey);
        var json = command.ExecuteScalar() as string;
        return string.IsNullOrWhiteSpace(json)
            ? new AppSettings()
            : JsonSerializer.Deserialize<AppSettings>(json, _json) ?? new AppSettings();
    }
}
