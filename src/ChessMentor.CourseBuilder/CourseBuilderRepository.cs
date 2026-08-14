using ChessMentor.Persistence;

namespace ChessMentor.CourseBuilder;

public sealed record CourseBuilderDocumentSummary(
    string Id,
    string Title,
    int Revision,
    DateTimeOffset UpdatedUtc,
    bool Dirty);

public sealed record CourseBuilderRevision(
    string CourseId,
    int Revision,
    string Reason,
    DateTimeOffset CreatedUtc);

public sealed class CourseBuilderRepository(AppDatabase database)
{
    public Task<IReadOnlyList<CourseBuilderDocumentSummary>> ListAsync(CancellationToken cancellationToken = default) =>
        database.ExecuteAsync<IReadOnlyList<CourseBuilderDocumentSummary>>(
            connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = """
                    SELECT id, title, current_revision, updated_utc, dirty
                    FROM course_builder_documents
                    ORDER BY updated_utc DESC, title COLLATE NOCASE;
                    """;
                using var reader = command.ExecuteReader();
                var results = new List<CourseBuilderDocumentSummary>();
                while (reader.Read())
                {
                    results.Add(new CourseBuilderDocumentSummary(
                        reader.GetString(0),
                        reader.GetString(1),
                        reader.GetInt32(2),
                        DateTimeOffset.Parse(reader.GetString(3)),
                        reader.GetInt32(4) != 0));
                }

                return results;
            },
            cancellationToken);

    public Task<CourseBuilderDocument?> GetAsync(string id, CancellationToken cancellationToken = default) =>
        database.ExecuteAsync(
            connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = """
                    SELECT document_json
                    FROM course_builder_documents
                    WHERE id = $id;
                    """;
                command.Parameters.AddWithValue("$id", id);
                var value = command.ExecuteScalar();
                return value is string json ? CourseBuilderJson.Deserialize(json) : null;
            },
            cancellationToken);

    public Task<int> SaveAsync(
        CourseBuilderDocument source,
        string reason,
        bool dirty = true,
        CancellationToken cancellationToken = default)
    {
        var document = source.Normalize() with { UpdatedUtc = DateTimeOffset.UtcNow };
        var json = CourseBuilderJson.Serialize(document);
        return database.ExecuteAsync(
            connection =>
            {
                using var transaction = connection.BeginTransaction();
                var revision = 1;
                using (var read = connection.CreateCommand())
                {
                    read.Transaction = transaction;
                    read.CommandText = "SELECT current_revision FROM course_builder_documents WHERE id = $id;";
                    read.Parameters.AddWithValue("$id", document.Id);
                    if (read.ExecuteScalar() is long current)
                    {
                        revision = checked((int)current + 1);
                    }
                }

                using (var upsert = connection.CreateCommand())
                {
                    upsert.Transaction = transaction;
                    upsert.CommandText = """
                        INSERT INTO course_builder_documents(
                            id, server_course_id, title, document_json,
                            current_revision, updated_utc, dirty)
                        VALUES($id, $server, $title, $json, $revision, $utc, $dirty)
                        ON CONFLICT(id) DO UPDATE SET
                            server_course_id = excluded.server_course_id,
                            title = excluded.title,
                            document_json = excluded.document_json,
                            current_revision = excluded.current_revision,
                            updated_utc = excluded.updated_utc,
                            dirty = excluded.dirty;
                        """;
                    upsert.Parameters.AddWithValue("$id", document.Id);
                    upsert.Parameters.AddWithValue("$server", (object?)document.ServerCourseId ?? DBNull.Value);
                    upsert.Parameters.AddWithValue("$title", document.Title);
                    upsert.Parameters.AddWithValue("$json", json);
                    upsert.Parameters.AddWithValue("$revision", revision);
                    upsert.Parameters.AddWithValue("$utc", document.UpdatedUtc.ToString("O"));
                    upsert.Parameters.AddWithValue("$dirty", dirty ? 1 : 0);
                    upsert.ExecuteNonQuery();
                }

                using (var revisionCommand = connection.CreateCommand())
                {
                    revisionCommand.Transaction = transaction;
                    revisionCommand.CommandText = """
                        INSERT INTO course_builder_revisions(
                            course_id, revision, document_json, reason, created_utc)
                        VALUES($course, $revision, $json, $reason, $utc);
                        """;
                    revisionCommand.Parameters.AddWithValue("$course", document.Id);
                    revisionCommand.Parameters.AddWithValue("$revision", revision);
                    revisionCommand.Parameters.AddWithValue("$json", json);
                    revisionCommand.Parameters.AddWithValue("$reason", reason ?? string.Empty);
                    revisionCommand.Parameters.AddWithValue("$utc", document.UpdatedUtc.ToString("O"));
                    revisionCommand.ExecuteNonQuery();
                }

                transaction.Commit();
                return revision;
            },
            cancellationToken);
    }

    public Task<IReadOnlyList<CourseBuilderRevision>> RevisionsAsync(
        string courseId,
        CancellationToken cancellationToken = default) =>
        database.ExecuteAsync<IReadOnlyList<CourseBuilderRevision>>(
            connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = """
                    SELECT course_id, revision, COALESCE(reason, ''), created_utc
                    FROM course_builder_revisions
                    WHERE course_id = $course
                    ORDER BY revision DESC;
                    """;
                command.Parameters.AddWithValue("$course", courseId);
                using var reader = command.ExecuteReader();
                var results = new List<CourseBuilderRevision>();
                while (reader.Read())
                {
                    results.Add(new CourseBuilderRevision(
                        reader.GetString(0),
                        reader.GetInt32(1),
                        reader.GetString(2),
                        DateTimeOffset.Parse(reader.GetString(3))));
                }

                return results;
            },
            cancellationToken);
}
