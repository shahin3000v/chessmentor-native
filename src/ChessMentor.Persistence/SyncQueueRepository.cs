using Microsoft.Data.Sqlite;

namespace ChessMentor.Persistence;

public sealed record SyncQueueItem(
    string Id,
    string OperationType,
    string EntityType,
    string EntityId,
    string PayloadJson,
    string? ExpectedRevision,
    int Attempts,
    DateTimeOffset NextAttemptUtc,
    string? LastError,
    DateTimeOffset CreatedUtc);

public sealed class SyncQueueRepository(AppDatabase database)
{
    public Task EnqueueAsync(
        string id,
        string operationType,
        string entityType,
        string entityId,
        string payloadJson,
        string? expectedRevision = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationType);
        ArgumentException.ThrowIfNullOrWhiteSpace(entityType);
        ArgumentException.ThrowIfNullOrWhiteSpace(entityId);
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadJson);
        return database.ExecuteAsync(
            connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = """
                    INSERT INTO sync_queue(
                        id, operation_type, entity_type, entity_id, payload_json,
                        expected_revision, attempts, next_attempt_utc, last_error, created_utc)
                    VALUES($id, $operation, $entityType, $entityId, $payload,
                           $revision, 0, $next, NULL, $created)
                    ON CONFLICT(id) DO UPDATE SET
                        operation_type = excluded.operation_type,
                        entity_type = excluded.entity_type,
                        entity_id = excluded.entity_id,
                        payload_json = excluded.payload_json,
                        expected_revision = excluded.expected_revision,
                        attempts = 0,
                        next_attempt_utc = excluded.next_attempt_utc,
                        last_error = NULL;
                    """;
                var now = DateTimeOffset.UtcNow.ToString("O");
                command.Parameters.AddWithValue("$id", id);
                command.Parameters.AddWithValue("$operation", operationType);
                command.Parameters.AddWithValue("$entityType", entityType);
                command.Parameters.AddWithValue("$entityId", entityId);
                command.Parameters.AddWithValue("$payload", payloadJson);
                command.Parameters.AddWithValue("$revision", (object?)expectedRevision ?? DBNull.Value);
                command.Parameters.AddWithValue("$next", now);
                command.Parameters.AddWithValue("$created", now);
                command.ExecuteNonQuery();
                return 0;
            },
            cancellationToken);
    }

    public Task<IReadOnlyList<SyncQueueItem>> ReadyAsync(
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        var safeLimit = Math.Clamp(limit, 1, 500);
        return database.ExecuteAsync<IReadOnlyList<SyncQueueItem>>(
            connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = """
                    SELECT id, operation_type, entity_type, entity_id, payload_json,
                           expected_revision, attempts, next_attempt_utc, last_error, created_utc
                    FROM sync_queue
                    WHERE next_attempt_utc <= $now
                    ORDER BY created_utc, id
                    LIMIT $limit;
                    """;
                command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
                command.Parameters.AddWithValue("$limit", safeLimit);
                using var reader = command.ExecuteReader();
                var result = new List<SyncQueueItem>();
                while (reader.Read())
                {
                    result.Add(Read(reader));
                }

                return result;
            },
            cancellationToken);
    }

    public Task CompleteAsync(string id, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        return database.ExecuteAsync(
            connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = "DELETE FROM sync_queue WHERE id = $id;";
                command.Parameters.AddWithValue("$id", id);
                command.ExecuteNonQuery();
                return 0;
            },
            cancellationToken);
    }

    public Task FailAsync(
        string id,
        string error,
        TimeSpan retryDelay,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        return database.ExecuteAsync(
            connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = """
                    UPDATE sync_queue
                    SET attempts = attempts + 1,
                        next_attempt_utc = $next,
                        last_error = $error
                    WHERE id = $id;
                    """;
                command.Parameters.AddWithValue("$id", id);
                command.Parameters.AddWithValue("$next", DateTimeOffset.UtcNow.Add(retryDelay).ToString("O"));
                command.Parameters.AddWithValue("$error", error ?? string.Empty);
                command.ExecuteNonQuery();
                return 0;
            },
            cancellationToken);
    }

    public Task<int> CountAsync(CancellationToken cancellationToken = default) =>
        database.ExecuteAsync(
            connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT COUNT(*) FROM sync_queue;";
                return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
            },
            cancellationToken);

    public Task<bool> ContainsAsync(string id, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        return database.ExecuteAsync(
            connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT EXISTS(SELECT 1 FROM sync_queue WHERE id = $id);";
                command.Parameters.AddWithValue("$id", id);
                return Convert.ToInt32(
                    command.ExecuteScalar(),
                    System.Globalization.CultureInfo.InvariantCulture) != 0;
            },
            cancellationToken);
    }

    private static SyncQueueItem Read(SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        reader.GetString(2),
        reader.GetString(3),
        reader.GetString(4),
        reader.IsDBNull(5) ? null : reader.GetString(5),
        reader.GetInt32(6),
        DateTimeOffset.Parse(reader.GetString(7)),
        reader.IsDBNull(8) ? null : reader.GetString(8),
        DateTimeOffset.Parse(reader.GetString(9)));
}
