using Microsoft.Data.Sqlite;

namespace ChessMentor.Persistence;

internal static class DatabaseMigrator
{
    internal const int CurrentVersion = 3;

    public static void Migrate(SqliteConnection connection)
    {
        using var transaction = connection.BeginTransaction();
        Execute(connection, transaction, """
            CREATE TABLE IF NOT EXISTS schema_migrations (
                version INTEGER PRIMARY KEY,
                applied_utc TEXT NOT NULL
            );
            """);

        var applied = ReadAppliedVersions(connection, transaction);
        if (!applied.Contains(1))
        {
            ApplyVersion1(connection, transaction);
            RecordVersion(connection, transaction, 1);
        }

        if (!applied.Contains(2))
        {
            ApplyVersion2(connection, transaction);
            RecordVersion(connection, transaction, 2);
        }

        if (!applied.Contains(3))
        {
            ApplyVersion3(connection, transaction);
            RecordVersion(connection, transaction, 3);
        }

        if (applied.Any(version => version > CurrentVersion))
        {
            throw new InvalidOperationException("The local database was created by a newer ChessMentor version.");
        }

        transaction.Commit();
    }

    private static HashSet<int> ReadAppliedVersions(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT version FROM schema_migrations;";
        using var reader = command.ExecuteReader();
        var versions = new HashSet<int>();
        while (reader.Read())
        {
            versions.Add(reader.GetInt32(0));
        }

        return versions;
    }

    private static void RecordVersion(SqliteConnection connection, SqliteTransaction transaction, int version)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO schema_migrations(version, applied_utc) VALUES($version, $utc);";
        command.Parameters.AddWithValue("$version", version);
        command.Parameters.AddWithValue("$utc", DateTimeOffset.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
    }

    private static void ApplyVersion1(SqliteConnection connection, SqliteTransaction transaction) =>
        Execute(connection, transaction, """
            CREATE TABLE settings (
                key TEXT PRIMARY KEY,
                json_value TEXT NOT NULL,
                updated_utc TEXT NOT NULL
            );

            CREATE TABLE local_drafts (
                id TEXT PRIMARY KEY,
                source_id TEXT,
                title TEXT NOT NULL DEFAULT '',
                payload_json TEXT NOT NULL,
                server_revision TEXT,
                updated_utc TEXT NOT NULL,
                dirty INTEGER NOT NULL DEFAULT 1
            );

            CREATE TABLE pgn_documents (
                id TEXT PRIMARY KEY,
                source_id TEXT,
                title TEXT NOT NULL DEFAULT '',
                pgn_text TEXT NOT NULL,
                metadata_json TEXT NOT NULL DEFAULT '{}',
                updated_utc TEXT NOT NULL
            );

            CREATE TABLE translation_cache (
                phrase_identity TEXT NOT NULL,
                source_language TEXT NOT NULL,
                target_language TEXT NOT NULL,
                source_text TEXT NOT NULL,
                translated_text TEXT NOT NULL,
                status TEXT NOT NULL,
                course_id TEXT,
                game_id TEXT,
                node_id TEXT,
                server_revision TEXT,
                updated_utc TEXT NOT NULL,
                PRIMARY KEY(phrase_identity, target_language)
            );

            CREATE TABLE course_builder_documents (
                id TEXT PRIMARY KEY,
                server_course_id TEXT,
                title TEXT NOT NULL DEFAULT '',
                document_json TEXT NOT NULL,
                current_revision INTEGER NOT NULL DEFAULT 0,
                updated_utc TEXT NOT NULL,
                dirty INTEGER NOT NULL DEFAULT 1
            );

            CREATE TABLE course_builder_revisions (
                course_id TEXT NOT NULL,
                revision INTEGER NOT NULL,
                document_json TEXT NOT NULL,
                reason TEXT,
                created_utc TEXT NOT NULL,
                PRIMARY KEY(course_id, revision),
                FOREIGN KEY(course_id) REFERENCES course_builder_documents(id) ON DELETE CASCADE
            );

            CREATE TABLE course_runtime_current_progress (
                course_id TEXT NOT NULL,
                user_id TEXT NOT NULL,
                attempt_id TEXT NOT NULL,
                stage_index INTEGER NOT NULL DEFAULT 0,
                state_json TEXT NOT NULL,
                updated_utc TEXT NOT NULL,
                PRIMARY KEY(course_id, user_id)
            );

            CREATE TABLE course_runtime_history (
                attempt_id TEXT PRIMARY KEY,
                course_id TEXT NOT NULL,
                user_id TEXT NOT NULL,
                started_utc TEXT NOT NULL,
                completed_utc TEXT,
                summary_json TEXT NOT NULL DEFAULT '{}'
            );

            CREATE TABLE move_trainer_courses (
                id TEXT PRIMARY KEY,
                source_id TEXT,
                title TEXT NOT NULL DEFAULT '',
                settings_json TEXT NOT NULL DEFAULT '{}',
                updated_utc TEXT NOT NULL
            );

            CREATE TABLE move_trainer_items (
                id TEXT PRIMARY KEY,
                course_id TEXT NOT NULL,
                game_id TEXT,
                node_id TEXT,
                fen TEXT NOT NULL,
                answer_json TEXT NOT NULL,
                transposition_key TEXT,
                updated_utc TEXT NOT NULL,
                FOREIGN KEY(course_id) REFERENCES move_trainer_courses(id) ON DELETE CASCADE
            );

            CREATE INDEX ix_move_trainer_items_course ON move_trainer_items(course_id);
            CREATE INDEX ix_move_trainer_items_transposition ON move_trainer_items(transposition_key);

            CREATE TABLE fsrs_state (
                user_id TEXT NOT NULL,
                item_id TEXT NOT NULL,
                due_utc TEXT NOT NULL,
                stability REAL NOT NULL,
                difficulty REAL NOT NULL,
                elapsed_days INTEGER NOT NULL,
                scheduled_days INTEGER NOT NULL,
                repetitions INTEGER NOT NULL,
                lapses INTEGER NOT NULL,
                last_review_utc TEXT,
                state_json TEXT NOT NULL DEFAULT '{}',
                PRIMARY KEY(user_id, item_id),
                FOREIGN KEY(item_id) REFERENCES move_trainer_items(id) ON DELETE CASCADE
            );

            CREATE INDEX ix_fsrs_due ON fsrs_state(user_id, due_utc);

            CREATE TABLE audio_metadata (
                id TEXT PRIMARY KEY,
                course_id TEXT,
                game_id TEXT,
                node_id TEXT,
                user_id TEXT,
                scope TEXT NOT NULL,
                local_path TEXT,
                server_id TEXT,
                duration_ms INTEGER,
                content_type TEXT,
                updated_utc TEXT NOT NULL,
                dirty INTEGER NOT NULL DEFAULT 0
            );

            CREATE TABLE sync_queue (
                id TEXT PRIMARY KEY,
                operation_type TEXT NOT NULL,
                entity_type TEXT NOT NULL,
                entity_id TEXT NOT NULL,
                payload_json TEXT NOT NULL,
                expected_revision TEXT,
                attempts INTEGER NOT NULL DEFAULT 0,
                next_attempt_utc TEXT NOT NULL,
                last_error TEXT,
                created_utc TEXT NOT NULL
            );

            CREATE INDEX ix_sync_queue_ready ON sync_queue(next_attempt_utc, attempts);

            CREATE TABLE sync_revisions (
                entity_type TEXT NOT NULL,
                entity_id TEXT NOT NULL,
                local_revision TEXT,
                server_revision TEXT,
                synced_utc TEXT NOT NULL,
                PRIMARY KEY(entity_type, entity_id)
            );
            """);

    private static void ApplyVersion2(SqliteConnection connection, SqliteTransaction transaction) =>
        Execute(connection, transaction, """
            ALTER TABLE local_drafts ADD COLUMN current_revision INTEGER NOT NULL DEFAULT 0;

            CREATE TABLE studio_draft_revisions (
                draft_id TEXT NOT NULL,
                revision INTEGER NOT NULL,
                payload_json TEXT NOT NULL,
                reason TEXT NOT NULL,
                created_utc TEXT NOT NULL,
                PRIMARY KEY(draft_id, revision),
                FOREIGN KEY(draft_id) REFERENCES local_drafts(id) ON DELETE CASCADE
            );

            CREATE INDEX ix_local_drafts_updated ON local_drafts(updated_utc DESC);
            CREATE INDEX ix_translation_cache_source ON translation_cache(source_text, target_language);
            CREATE INDEX ix_translation_cache_updated ON translation_cache(updated_utc DESC);
            """);

    private static void ApplyVersion3(SqliteConnection connection, SqliteTransaction transaction) =>
        Execute(connection, transaction, """
            CREATE TABLE translation_cache_usages (
                phrase_identity TEXT NOT NULL,
                target_language TEXT NOT NULL,
                course_id TEXT NOT NULL DEFAULT '',
                game_id TEXT NOT NULL,
                node_id TEXT NOT NULL,
                comment_field TEXT NOT NULL,
                updated_utc TEXT NOT NULL,
                PRIMARY KEY(
                    phrase_identity, target_language, course_id,
                    game_id, node_id, comment_field)
            );

            CREATE INDEX ix_translation_cache_usages_location
                ON translation_cache_usages(course_id, game_id, node_id, comment_field);
            """);

    private static void Execute(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
