using Microsoft.Data.Sqlite;

namespace ChessMentor.Persistence;

public sealed record TranslationCacheEntry(
    string PhraseIdentity,
    string SourceLanguage,
    string TargetLanguage,
    string SourceText,
    string TranslatedText,
    string Status,
    string? CourseId,
    string? GameId,
    string? NodeId,
    string? ServerRevision,
    DateTimeOffset UpdatedUtc);

public sealed record TranslationCacheUsage(
    string PhraseIdentity,
    string TargetLanguage,
    string? CourseId,
    string GameId,
    string NodeId,
    string Field,
    DateTimeOffset UpdatedUtc);

public sealed class TranslationCacheRepository(AppDatabase database)
{
    private const int LookupBatchSize = 400;

    public Task<IReadOnlyDictionary<string, TranslationCacheEntry>> GetManyAsync(
        IReadOnlyCollection<string> phraseIdentities,
        string targetLanguage,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(phraseIdentities);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetLanguage);
        var identities = phraseIdentities.Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return database.ExecuteAsync<IReadOnlyDictionary<string, TranslationCacheEntry>>(
            connection => Lookup(connection, identities, targetLanguage),
            cancellationToken);
    }

    public Task UpsertManyAsync(
        IReadOnlyCollection<TranslationCacheEntry> entries,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (entries.Count == 0)
        {
            return Task.CompletedTask;
        }

        return database.ExecuteAsync(
            connection =>
            {
                using var transaction = connection.BeginTransaction();
                foreach (var entry in entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    using var command = connection.CreateCommand();
                    command.Transaction = transaction;
                    command.CommandText = """
                        INSERT INTO translation_cache(
                            phrase_identity, source_language, target_language, source_text,
                            translated_text, status, course_id, game_id, node_id,
                            server_revision, updated_utc)
                        VALUES($identity, $sourceLanguage, $targetLanguage, $sourceText,
                               $translatedText, $status, $courseId, $gameId, $nodeId,
                               $serverRevision, $updated)
                        ON CONFLICT(phrase_identity, target_language) DO UPDATE SET
                            source_language = excluded.source_language,
                            source_text = excluded.source_text,
                            translated_text = excluded.translated_text,
                            status = excluded.status,
                            course_id = COALESCE(excluded.course_id, translation_cache.course_id),
                            game_id = COALESCE(excluded.game_id, translation_cache.game_id),
                            node_id = COALESCE(excluded.node_id, translation_cache.node_id),
                            server_revision = COALESCE(excluded.server_revision, translation_cache.server_revision),
                            updated_utc = excluded.updated_utc;
                        """;
                    command.Parameters.AddWithValue("$identity", entry.PhraseIdentity);
                    command.Parameters.AddWithValue("$sourceLanguage", entry.SourceLanguage);
                    command.Parameters.AddWithValue("$targetLanguage", entry.TargetLanguage);
                    command.Parameters.AddWithValue("$sourceText", entry.SourceText);
                    command.Parameters.AddWithValue("$translatedText", entry.TranslatedText);
                    command.Parameters.AddWithValue("$status", entry.Status);
                    command.Parameters.AddWithValue("$courseId", (object?)entry.CourseId ?? DBNull.Value);
                    command.Parameters.AddWithValue("$gameId", (object?)entry.GameId ?? DBNull.Value);
                    command.Parameters.AddWithValue("$nodeId", (object?)entry.NodeId ?? DBNull.Value);
                    command.Parameters.AddWithValue("$serverRevision", (object?)entry.ServerRevision ?? DBNull.Value);
                    command.Parameters.AddWithValue("$updated", entry.UpdatedUtc.ToString("O"));
                    command.ExecuteNonQuery();
                }

                transaction.Commit();
                return 0;
            },
            cancellationToken);
    }

    public Task<IReadOnlyList<TranslationCacheEntry>> SearchAsync(
        string query,
        int offset,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var safeOffset = Math.Max(0, offset);
        var safeLimit = Math.Clamp(limit, 1, 500);
        return database.ExecuteAsync<IReadOnlyList<TranslationCacheEntry>>(
            connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = """
                    SELECT phrase_identity, source_language, target_language, source_text,
                           translated_text, status, course_id, game_id, node_id,
                           server_revision, updated_utc
                    FROM translation_cache
                    WHERE $query = '' OR source_text LIKE $like OR translated_text LIKE $like
                    ORDER BY updated_utc DESC
                    LIMIT $limit OFFSET $offset;
                    """;
                command.Parameters.AddWithValue("$query", query ?? string.Empty);
                command.Parameters.AddWithValue("$like", $"%{query ?? string.Empty}%");
                command.Parameters.AddWithValue("$limit", safeLimit);
                command.Parameters.AddWithValue("$offset", safeOffset);
                using var reader = command.ExecuteReader();
                var result = new List<TranslationCacheEntry>();
                while (reader.Read())
                {
                    result.Add(Read(reader));
                }

                return result;
            },
            cancellationToken);
    }

    public Task UpsertUsagesAsync(
        IReadOnlyCollection<TranslationCacheUsage> usages,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(usages);
        if (usages.Count == 0)
        {
            return Task.CompletedTask;
        }

        return database.ExecuteAsync(
            connection =>
            {
                using var transaction = connection.BeginTransaction();
                foreach (var usage in usages)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    using var command = connection.CreateCommand();
                    command.Transaction = transaction;
                    command.CommandText = """
                        INSERT INTO translation_cache_usages(
                            phrase_identity, target_language, course_id,
                            game_id, node_id, comment_field, updated_utc)
                        VALUES($identity, $target, $course, $game, $node, $field, $updated)
                        ON CONFLICT(
                            phrase_identity, target_language, course_id,
                            game_id, node_id, comment_field)
                        DO UPDATE SET updated_utc = excluded.updated_utc;
                        """;
                    command.Parameters.AddWithValue("$identity", usage.PhraseIdentity);
                    command.Parameters.AddWithValue("$target", usage.TargetLanguage);
                    command.Parameters.AddWithValue("$course", usage.CourseId ?? string.Empty);
                    command.Parameters.AddWithValue("$game", usage.GameId);
                    command.Parameters.AddWithValue("$node", usage.NodeId);
                    command.Parameters.AddWithValue("$field", usage.Field);
                    command.Parameters.AddWithValue("$updated", usage.UpdatedUtc.ToString("O"));
                    command.ExecuteNonQuery();
                }

                transaction.Commit();
                return 0;
            },
            cancellationToken);
    }

    public Task<IReadOnlyList<TranslationCacheUsage>> ListUsagesAsync(
        string phraseIdentity,
        string targetLanguage,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(phraseIdentity);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetLanguage);
        return database.ExecuteAsync<IReadOnlyList<TranslationCacheUsage>>(
            connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = """
                    SELECT phrase_identity, target_language, course_id,
                           game_id, node_id, comment_field, updated_utc
                    FROM translation_cache_usages
                    WHERE phrase_identity = $identity AND target_language = $target
                    ORDER BY course_id, game_id, node_id, comment_field;
                    """;
                command.Parameters.AddWithValue("$identity", phraseIdentity);
                command.Parameters.AddWithValue("$target", targetLanguage);
                using var reader = command.ExecuteReader();
                var result = new List<TranslationCacheUsage>();
                while (reader.Read())
                {
                    result.Add(new TranslationCacheUsage(
                        reader.GetString(0),
                        reader.GetString(1),
                        string.IsNullOrEmpty(reader.GetString(2)) ? null : reader.GetString(2),
                        reader.GetString(3),
                        reader.GetString(4),
                        reader.GetString(5),
                        DateTimeOffset.Parse(reader.GetString(6))));
                }

                return result;
            },
            cancellationToken);
    }

    private static IReadOnlyDictionary<string, TranslationCacheEntry> Lookup(
        SqliteConnection connection,
        IReadOnlyList<string> identities,
        string targetLanguage)
    {
        var result = new Dictionary<string, TranslationCacheEntry>(StringComparer.Ordinal);
        for (var start = 0; start < identities.Count; start += LookupBatchSize)
        {
            var count = Math.Min(LookupBatchSize, identities.Count - start);
            using var command = connection.CreateCommand();
            var parameters = new string[count];
            for (var index = 0; index < count; index++)
            {
                parameters[index] = $"$id{index}";
                command.Parameters.AddWithValue(parameters[index], identities[start + index]);
            }

            command.CommandText = $"""
                SELECT phrase_identity, source_language, target_language, source_text,
                       translated_text, status, course_id, game_id, node_id,
                       server_revision, updated_utc
                FROM translation_cache
                WHERE target_language = $target AND phrase_identity IN ({string.Join(',', parameters)});
                """;
            command.Parameters.AddWithValue("$target", targetLanguage);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var entry = Read(reader);
                result[entry.PhraseIdentity] = entry;
            }
        }

        return result;
    }

    private static TranslationCacheEntry Read(SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        reader.GetString(2),
        reader.GetString(3),
        reader.GetString(4),
        reader.GetString(5),
        reader.IsDBNull(6) ? null : reader.GetString(6),
        reader.IsDBNull(7) ? null : reader.GetString(7),
        reader.IsDBNull(8) ? null : reader.GetString(8),
        reader.IsDBNull(9) ? null : reader.GetString(9),
        DateTimeOffset.Parse(reader.GetString(10)));
}
