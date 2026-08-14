using Microsoft.Data.Sqlite;

namespace ChessMentor.Persistence;

internal static class DatabaseMigrator
{
    internal const int CurrentVersion = 4;

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

        if (!applied.Contains(4))
        {
            ApplyVersion4(connection, transaction);
            RecordVersion(connection, transaction, 4);
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

    private static void ApplyVersion4(SqliteConnection connection, SqliteTransaction transaction) =>
        Execute(connection, transaction, """
            ALTER TABLE move_trainer_courses ADD COLUMN source_pgn TEXT NOT NULL DEFAULT '';

            CREATE TABLE practice_attempts (
                id TEXT PRIMARY KEY,
                user_id TEXT NOT NULL,
                course_id TEXT,
                item_id TEXT,
                block_id TEXT NOT NULL DEFAULT '',
                block_type TEXT NOT NULL DEFAULT 'interactive-move',
                source_kind TEXT NOT NULL CHECK(source_kind IN ('course_runtime','move_trainer')),
                attempt_kind TEXT NOT NULL DEFAULT 'review',
                card_key TEXT NOT NULL DEFAULT '',
                start_fen TEXT NOT NULL,
                result_fen TEXT NOT NULL DEFAULT '',
                move_uci TEXT NOT NULL DEFAULT '',
                move_san TEXT NOT NULL DEFAULT '',
                selected_piece TEXT NOT NULL DEFAULT '',
                from_square TEXT NOT NULL DEFAULT '',
                to_square TEXT NOT NULL DEFAULT '',
                input_method TEXT NOT NULL DEFAULT 'click',
                hints_used INTEGER NOT NULL DEFAULT 0,
                response_ms INTEGER NOT NULL DEFAULT 0,
                outcome TEXT NOT NULL CHECK(outcome IN ('correct','soft_fail','wrong')),
                is_correct INTEGER NOT NULL DEFAULT 0,
                score INTEGER NOT NULL DEFAULT 0,
                grade TEXT NOT NULL DEFAULT '',
                feedback TEXT NOT NULL DEFAULT '',
                payload_json TEXT NOT NULL DEFAULT '{}',
                created_utc TEXT NOT NULL,
                FOREIGN KEY(item_id) REFERENCES move_trainer_items(id) ON DELETE SET NULL
            );

            CREATE INDEX ix_practice_attempts_user_created
                ON practice_attempts(user_id, created_utc DESC);
            CREATE INDEX ix_practice_attempts_course
                ON practice_attempts(user_id, course_id, created_utc DESC);
            CREATE INDEX ix_practice_attempts_item
                ON practice_attempts(user_id, item_id, created_utc DESC);

            CREATE TABLE practice_cards (
                id TEXT PRIMARY KEY,
                user_id TEXT NOT NULL,
                course_id TEXT,
                item_id TEXT,
                block_id TEXT NOT NULL DEFAULT '',
                block_type TEXT NOT NULL DEFAULT 'interactive-move',
                card_key TEXT NOT NULL,
                prompt TEXT NOT NULL DEFAULT '',
                fen TEXT NOT NULL,
                orientation TEXT NOT NULL DEFAULT 'white',
                expected_json TEXT NOT NULL DEFAULT '[]',
                source_json TEXT NOT NULL DEFAULT '{}',
                mistake_count INTEGER NOT NULL DEFAULT 0,
                success_count INTEGER NOT NULL DEFAULT 0,
                soft_fail_count INTEGER NOT NULL DEFAULT 0,
                fsrs_state TEXT NOT NULL DEFAULT 'new',
                fsrs_step INTEGER,
                stability REAL NOT NULL DEFAULT 0,
                difficulty REAL NOT NULL DEFAULT 5,
                retrievability REAL NOT NULL DEFAULT 0,
                due_utc TEXT NOT NULL,
                last_review_utc TEXT,
                last_source_kind TEXT NOT NULL DEFAULT 'move_trainer',
                created_utc TEXT NOT NULL,
                updated_utc TEXT NOT NULL,
                UNIQUE(user_id, card_key),
                FOREIGN KEY(item_id) REFERENCES move_trainer_items(id) ON DELETE SET NULL
            );

            CREATE INDEX ix_practice_cards_due
                ON practice_cards(user_id, due_utc, updated_utc DESC);
            CREATE INDEX ix_practice_cards_course
                ON practice_cards(user_id, course_id, updated_utc DESC);

            CREATE TABLE practice_reviews (
                id TEXT PRIMARY KEY,
                user_id TEXT NOT NULL,
                card_id TEXT NOT NULL,
                course_id TEXT,
                source_kind TEXT NOT NULL CHECK(source_kind IN ('course_runtime','move_trainer')),
                move_uci TEXT NOT NULL DEFAULT '',
                move_san TEXT NOT NULL DEFAULT '',
                outcome TEXT NOT NULL CHECK(outcome IN ('correct','soft_fail','wrong')),
                requested_rating TEXT NOT NULL,
                applied_rating TEXT NOT NULL,
                response_ms INTEGER NOT NULL DEFAULT 0,
                fsrs_before_json TEXT NOT NULL DEFAULT '{}',
                fsrs_after_json TEXT NOT NULL DEFAULT '{}',
                review_log_json TEXT NOT NULL DEFAULT '{}',
                created_utc TEXT NOT NULL,
                FOREIGN KEY(card_id) REFERENCES practice_cards(id) ON DELETE CASCADE
            );

            CREATE INDEX ix_practice_reviews_card
                ON practice_reviews(card_id, created_utc DESC);

            CREATE TABLE practice_attempt_contexts (
                attempt_id TEXT PRIMARY KEY,
                block_snapshot_json TEXT NOT NULL DEFAULT '{}',
                input_method TEXT NOT NULL DEFAULT 'click',
                hints_used INTEGER NOT NULL DEFAULT 0,
                client_data_json TEXT NOT NULL DEFAULT '{}',
                created_utc TEXT NOT NULL,
                FOREIGN KEY(attempt_id) REFERENCES practice_attempts(id) ON DELETE CASCADE
            );

            CREATE TABLE move_trainer_profiles (
                user_id TEXT NOT NULL,
                course_id TEXT NOT NULL,
                profile_version INTEGER NOT NULL DEFAULT 1,
                first_course_data_utc TEXT,
                last_course_data_utc TEXT,
                first_trainer_data_utc TEXT,
                last_trainer_data_utc TEXT,
                course_attempts INTEGER NOT NULL DEFAULT 0,
                trainer_attempts INTEGER NOT NULL DEFAULT 0,
                total_attempts INTEGER NOT NULL DEFAULT 0,
                last_source_kind TEXT NOT NULL DEFAULT 'move_trainer',
                metadata_json TEXT NOT NULL DEFAULT '{}',
                created_utc TEXT NOT NULL,
                updated_utc TEXT NOT NULL,
                PRIMARY KEY(user_id, course_id)
            );

            CREATE INDEX ix_move_trainer_profiles_user
                ON move_trainer_profiles(user_id, updated_utc DESC, course_id);

            CREATE TABLE move_trainer_sessions (
                id TEXT PRIMARY KEY,
                user_id TEXT NOT NULL,
                course_id TEXT NOT NULL,
                mode TEXT NOT NULL DEFAULT 'due',
                status TEXT NOT NULL DEFAULT 'active',
                current_index INTEGER NOT NULL DEFAULT 0,
                started_utc TEXT NOT NULL,
                completed_utc TEXT,
                updated_utc TEXT NOT NULL,
                FOREIGN KEY(course_id) REFERENCES move_trainer_courses(id) ON DELETE CASCADE
            );

            CREATE INDEX ix_move_trainer_sessions_active
                ON move_trainer_sessions(user_id, course_id, status, updated_utc DESC);

            CREATE TABLE move_trainer_session_items (
                session_id TEXT NOT NULL,
                ordinal INTEGER NOT NULL,
                item_id TEXT NOT NULL,
                status TEXT NOT NULL DEFAULT 'pending',
                attempt_count INTEGER NOT NULL DEFAULT 0,
                last_outcome TEXT,
                had_mistake INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY(session_id, ordinal),
                FOREIGN KEY(session_id) REFERENCES move_trainer_sessions(id) ON DELETE CASCADE,
                FOREIGN KEY(item_id) REFERENCES move_trainer_items(id) ON DELETE CASCADE
            );

            CREATE TABLE move_trainer_migration_state (
                source_kind TEXT NOT NULL,
                source_id TEXT NOT NULL,
                target_id TEXT NOT NULL,
                migrated_utc TEXT NOT NULL,
                PRIMARY KEY(source_kind, source_id)
            );

            INSERT OR IGNORE INTO practice_cards(
                id, user_id, course_id, item_id, block_id, card_key, prompt, fen,
                orientation, expected_json, mistake_count, success_count,
                fsrs_state, stability, difficulty, retrievability, due_utc,
                last_review_utc, created_utc, updated_utc)
            SELECT
                'legacy-card:' || fs.user_id || ':' || fs.item_id,
                fs.user_id,
                item.course_id,
                fs.item_id,
                fs.item_id,
                'legacy:' || item.course_id || ':' || fs.item_id,
                'حرکت صحیح را پیدا کنید.',
                item.fen,
                CASE WHEN instr(item.fen, ' b ') > 0 THEN 'black' ELSE 'white' END,
                item.answer_json,
                fs.lapses,
                CASE WHEN fs.repetitions > fs.lapses THEN fs.repetitions - fs.lapses ELSE 0 END,
                CASE WHEN fs.repetitions > 0 THEN 'review' ELSE 'new' END,
                fs.stability,
                fs.difficulty,
                0,
                fs.due_utc,
                fs.last_review_utc,
                COALESCE(fs.last_review_utc, fs.due_utc),
                COALESCE(fs.last_review_utc, fs.due_utc)
            FROM fsrs_state fs
            JOIN move_trainer_items item ON item.id = fs.item_id;

            INSERT OR IGNORE INTO move_trainer_migration_state(
                source_kind, source_id, target_id, migrated_utc)
            SELECT
                'legacy-fsrs',
                fs.user_id || ':' || fs.item_id,
                'legacy-card:' || fs.user_id || ':' || fs.item_id,
                COALESCE(fs.last_review_utc, fs.due_utc)
            FROM fsrs_state fs;
            """);

    private static void Execute(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
