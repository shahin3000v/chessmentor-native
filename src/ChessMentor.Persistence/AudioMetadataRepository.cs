using Microsoft.Data.Sqlite;

namespace ChessMentor.Persistence;

public sealed record AudioMetadataRecord(
    string Id,
    string? CourseId,
    string? GameId,
    string? NodeId,
    string? UserId,
    string Scope,
    string? LocalPath,
    string? ServerId,
    long DurationMilliseconds,
    string? ContentType,
    DateTimeOffset UpdatedUtc,
    bool Dirty);

public sealed class AudioMetadataRepository(AppDatabase database)
{
    public Task UpsertAsync(
        AudioMetadataRecord record,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentException.ThrowIfNullOrWhiteSpace(record.Id);
        if (record.Scope is not ("course" or "user"))
        {
            throw new ArgumentOutOfRangeException(nameof(record), "Audio scope must be course or user.");
        }

        return database.ExecuteAsync(
            connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = """
                    INSERT INTO audio_metadata(
                        id, course_id, game_id, node_id, user_id, scope, local_path,
                        server_id, duration_ms, content_type, updated_utc, dirty)
                    VALUES($id, $course, $game, $node, $user, $scope, $path,
                           $server, $duration, $contentType, $updated, $dirty)
                    ON CONFLICT(id) DO UPDATE SET
                        course_id = excluded.course_id,
                        game_id = excluded.game_id,
                        node_id = excluded.node_id,
                        user_id = excluded.user_id,
                        scope = excluded.scope,
                        local_path = COALESCE(excluded.local_path, audio_metadata.local_path),
                        server_id = COALESCE(excluded.server_id, audio_metadata.server_id),
                        duration_ms = excluded.duration_ms,
                        content_type = COALESCE(excluded.content_type, audio_metadata.content_type),
                        updated_utc = excluded.updated_utc,
                        dirty = excluded.dirty;
                    """;
                command.Parameters.AddWithValue("$id", record.Id);
                command.Parameters.AddWithValue("$course", (object?)record.CourseId ?? DBNull.Value);
                command.Parameters.AddWithValue("$game", (object?)record.GameId ?? DBNull.Value);
                command.Parameters.AddWithValue("$node", (object?)record.NodeId ?? DBNull.Value);
                command.Parameters.AddWithValue("$user", (object?)record.UserId ?? DBNull.Value);
                command.Parameters.AddWithValue("$scope", record.Scope);
                command.Parameters.AddWithValue("$path", (object?)record.LocalPath ?? DBNull.Value);
                command.Parameters.AddWithValue("$server", (object?)record.ServerId ?? DBNull.Value);
                command.Parameters.AddWithValue("$duration", Math.Max(0, record.DurationMilliseconds));
                command.Parameters.AddWithValue("$contentType", (object?)record.ContentType ?? DBNull.Value);
                command.Parameters.AddWithValue("$updated", record.UpdatedUtc.ToString("O"));
                command.Parameters.AddWithValue("$dirty", record.Dirty ? 1 : 0);
                command.ExecuteNonQuery();
                return 0;
            },
            cancellationToken);
    }

    public Task<AudioMetadataRecord?> GetAsync(
        string id,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        return database.ExecuteAsync<AudioMetadataRecord?>(
            connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = SelectColumns + " WHERE id = $id;";
                command.Parameters.AddWithValue("$id", id);
                using var reader = command.ExecuteReader();
                return reader.Read() ? Read(reader) : null;
            },
            cancellationToken);
    }

    public Task<IReadOnlyList<AudioMetadataRecord>> ListForNodeAsync(
        string courseId,
        string gameId,
        string nodeId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(courseId);
        ArgumentException.ThrowIfNullOrWhiteSpace(gameId);
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeId);
        return database.ExecuteAsync<IReadOnlyList<AudioMetadataRecord>>(
            connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = SelectColumns + "\n" + """
                    WHERE course_id = $course AND game_id = $game AND node_id = $node
                    ORDER BY scope, updated_utc DESC;
                    """;
                command.Parameters.AddWithValue("$course", courseId);
                command.Parameters.AddWithValue("$game", gameId);
                command.Parameters.AddWithValue("$node", nodeId);
                using var reader = command.ExecuteReader();
                var result = new List<AudioMetadataRecord>();
                while (reader.Read())
                {
                    result.Add(Read(reader));
                }

                return result;
            },
            cancellationToken);
    }

    public Task<AudioMetadataRecord?> FindByServerIdAsync(
        string courseId,
        string serverId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(courseId);
        ArgumentException.ThrowIfNullOrWhiteSpace(serverId);
        return database.ExecuteAsync<AudioMetadataRecord?>(
            connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = SelectColumns + " WHERE course_id = $course AND server_id = $server LIMIT 1;";
                command.Parameters.AddWithValue("$course", courseId);
                command.Parameters.AddWithValue("$server", serverId);
                using var reader = command.ExecuteReader();
                return reader.Read() ? Read(reader) : null;
            },
            cancellationToken);
    }

    public Task DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        return database.ExecuteAsync(
            connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = "DELETE FROM audio_metadata WHERE id = $id;";
                command.Parameters.AddWithValue("$id", id);
                command.ExecuteNonQuery();
                return 0;
            },
            cancellationToken);
    }

    private const string SelectColumns = """
        SELECT id, course_id, game_id, node_id, user_id, scope, local_path,
               server_id, duration_ms, content_type, updated_utc, dirty
        FROM audio_metadata
        """;

    private static AudioMetadataRecord Read(SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.IsDBNull(1) ? null : reader.GetString(1),
        reader.IsDBNull(2) ? null : reader.GetString(2),
        reader.IsDBNull(3) ? null : reader.GetString(3),
        reader.IsDBNull(4) ? null : reader.GetString(4),
        reader.GetString(5),
        reader.IsDBNull(6) ? null : reader.GetString(6),
        reader.IsDBNull(7) ? null : reader.GetString(7),
        reader.GetInt64(8),
        reader.IsDBNull(9) ? null : reader.GetString(9),
        DateTimeOffset.Parse(reader.GetString(10)),
        reader.GetInt32(11) != 0);
}
