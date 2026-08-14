using Microsoft.Data.Sqlite;

namespace ChessMentor.Persistence;

public sealed record LocalDraftRecord(
    string Id,
    string? SourceId,
    string Title,
    string PayloadJson,
    string? ServerRevision,
    DateTimeOffset UpdatedUtc,
    bool Dirty,
    int CurrentRevision);

public sealed class LocalDraftRepository(AppDatabase database)
{
    /// <summary>
    /// Returns lightweight rows for the Studio picker. Large PGN payloads are
    /// deliberately omitted and loaded by <see cref="GetAsync"/> only when a
    /// draft is resumed.
    /// </summary>
    public Task<IReadOnlyList<LocalDraftRecord>> ListSummariesAsync(
        CancellationToken cancellationToken = default)
    {
        return database.ExecuteAsync<IReadOnlyList<LocalDraftRecord>>(
            connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = """
                    SELECT id, source_id, title, '' AS payload_json, server_revision,
                           updated_utc, dirty, current_revision
                    FROM local_drafts
                    ORDER BY updated_utc DESC;
                    """;
                using var reader = command.ExecuteReader();
                var result = new List<LocalDraftRecord>();
                while (reader.Read())
                {
                    result.Add(Read(reader));
                }

                return result;
            },
            cancellationToken);
    }

    public Task<IReadOnlyList<LocalDraftRecord>> ListAsync(CancellationToken cancellationToken = default) =>
        database.ExecuteAsync<IReadOnlyList<LocalDraftRecord>>(
            connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = """
                    SELECT id, source_id, title, payload_json, server_revision,
                           updated_utc, dirty, current_revision
                    FROM local_drafts
                    ORDER BY updated_utc DESC;
                    """;
                using var reader = command.ExecuteReader();
                var result = new List<LocalDraftRecord>();
                while (reader.Read())
                {
                    result.Add(Read(reader));
                }

                return result;
            },
            cancellationToken);

    public Task<LocalDraftRecord?> GetAsync(string id, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        return database.ExecuteAsync<LocalDraftRecord?>(
            connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = """
                    SELECT id, source_id, title, payload_json, server_revision,
                           updated_utc, dirty, current_revision
                    FROM local_drafts WHERE id = $id;
                    """;
                command.Parameters.AddWithValue("$id", id);
                using var reader = command.ExecuteReader();
                return reader.Read() ? Read(reader) : null;
            },
            cancellationToken);
    }

    public Task<LocalDraftRecord> SaveAsync(
        string id,
        string? sourceId,
        string title,
        string payloadJson,
        string reason,
        bool dirty = true,
        string? serverRevision = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(title);
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadJson);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return database.ExecuteAsync(
            connection => Save(connection, id, sourceId, title, payloadJson, reason, dirty, serverRevision),
            cancellationToken);
    }

    public Task DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        return database.ExecuteAsync(
            connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = "DELETE FROM local_drafts WHERE id = $id;";
                command.Parameters.AddWithValue("$id", id);
                command.ExecuteNonQuery();
                return 0;
            },
            cancellationToken);
    }

    public Task<IReadOnlyList<LocalDraftRevision>> RevisionsAsync(
        string draftId,
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(draftId);
        var safeLimit = Math.Clamp(limit, 1, 500);
        return database.ExecuteAsync<IReadOnlyList<LocalDraftRevision>>(
            connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = """
                    SELECT draft_id, revision, payload_json, reason, created_utc
                    FROM studio_draft_revisions
                    WHERE draft_id = $id
                    ORDER BY revision DESC
                    LIMIT $limit;
                    """;
                command.Parameters.AddWithValue("$id", draftId);
                command.Parameters.AddWithValue("$limit", safeLimit);
                using var reader = command.ExecuteReader();
                var result = new List<LocalDraftRevision>();
                while (reader.Read())
                {
                    result.Add(new LocalDraftRevision(
                        reader.GetString(0),
                        reader.GetInt32(1),
                        reader.GetString(2),
                        reader.GetString(3),
                        DateTimeOffset.Parse(reader.GetString(4))));
                }

                return result;
            },
            cancellationToken);
    }

    private static LocalDraftRecord Save(
        SqliteConnection connection,
        string id,
        string? sourceId,
        string title,
        string payloadJson,
        string reason,
        bool dirty,
        string? serverRevision)
    {
        using var transaction = connection.BeginTransaction();
        var revision = 1;
        using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = "SELECT current_revision FROM local_drafts WHERE id = $id;";
            read.Parameters.AddWithValue("$id", id);
            var current = read.ExecuteScalar();
            if (current is long value)
            {
                revision = checked((int)value + 1);
            }
        }

        var utc = DateTimeOffset.UtcNow;
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO local_drafts(
                    id, source_id, title, payload_json, server_revision,
                    updated_utc, dirty, current_revision)
                VALUES($id, $source, $title, $payload, $server, $utc, $dirty, $revision)
                ON CONFLICT(id) DO UPDATE SET
                    source_id = excluded.source_id,
                    title = excluded.title,
                    payload_json = excluded.payload_json,
                    server_revision = excluded.server_revision,
                    updated_utc = excluded.updated_utc,
                    dirty = excluded.dirty,
                    current_revision = excluded.current_revision;
                """;
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$source", (object?)sourceId ?? DBNull.Value);
            command.Parameters.AddWithValue("$title", title);
            command.Parameters.AddWithValue("$payload", payloadJson);
            command.Parameters.AddWithValue("$server", (object?)serverRevision ?? DBNull.Value);
            command.Parameters.AddWithValue("$utc", utc.ToString("O"));
            command.Parameters.AddWithValue("$dirty", dirty ? 1 : 0);
            command.Parameters.AddWithValue("$revision", revision);
            command.ExecuteNonQuery();
        }

        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO studio_draft_revisions(
                    draft_id, revision, payload_json, reason, created_utc)
                VALUES($id, $revision, $payload, $reason, $utc);
                """;
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$revision", revision);
            command.Parameters.AddWithValue("$payload", payloadJson);
            command.Parameters.AddWithValue("$reason", reason);
            command.Parameters.AddWithValue("$utc", utc.ToString("O"));
            command.ExecuteNonQuery();
        }

        transaction.Commit();
        return new LocalDraftRecord(id, sourceId, title, payloadJson, serverRevision, utc, dirty, revision);
    }

    private static LocalDraftRecord Read(SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.IsDBNull(1) ? null : reader.GetString(1),
        reader.GetString(2),
        reader.GetString(3),
        reader.IsDBNull(4) ? null : reader.GetString(4),
        DateTimeOffset.Parse(reader.GetString(5)),
        reader.GetInt32(6) != 0,
        reader.GetInt32(7));
}

public sealed record LocalDraftRevision(
    string DraftId,
    int Revision,
    string PayloadJson,
    string Reason,
    DateTimeOffset CreatedUtc);
