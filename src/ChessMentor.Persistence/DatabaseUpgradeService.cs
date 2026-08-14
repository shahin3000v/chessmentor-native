using System.Globalization;
using Microsoft.Data.Sqlite;

namespace ChessMentor.Persistence;

public sealed record DatabaseUpgradeTableResult(
    string Table,
    long SourceRows,
    long ImportedOrUpdatedRows);

public sealed record DatabaseUpgradeResult(
    int SourceSchemaVersion,
    IReadOnlyList<DatabaseUpgradeTableResult> Tables)
{
    public long SourceRows => Tables.Sum(static table => table.SourceRows);
    public long ImportedOrUpdatedRows => Tables.Sum(static table => table.ImportedOrUpdatedRows);
    public long UnchangedRows => Math.Max(0, SourceRows - ImportedOrUpdatedRows);
}

public sealed class DatabaseUpgradeException(string message, Exception? innerException = null)
    : InvalidOperationException(message, innerException);

/// <summary>
/// Imports another versioned ChessMentor SQLite database into the active one.
/// The selected file is first backed up to a temporary snapshot and migrated
/// there, so the user's source database is never modified. The final merge is a
/// single transaction and keeps the newest timestamped entity on key conflicts.
/// </summary>
public sealed class DatabaseUpgradeService(AppDatabase targetDatabase)
{
    private static readonly IReadOnlyList<ImportPlan> ImportPlans =
    [
        new("settings", """
            INSERT INTO settings(key, json_value, updated_utc)
            SELECT key, json_value, updated_utc FROM incoming.settings WHERE TRUE
            ON CONFLICT(key) DO UPDATE SET
                json_value = excluded.json_value,
                updated_utc = excluded.updated_utc
            WHERE excluded.updated_utc > settings.updated_utc;
            """),
        new("local_drafts", """
            INSERT INTO local_drafts(
                id, source_id, title, payload_json, server_revision,
                updated_utc, dirty, current_revision)
            SELECT id, source_id, title, payload_json, server_revision,
                   updated_utc, dirty, current_revision
            FROM incoming.local_drafts WHERE TRUE
            ON CONFLICT(id) DO UPDATE SET
                source_id = excluded.source_id,
                title = excluded.title,
                payload_json = excluded.payload_json,
                server_revision = excluded.server_revision,
                updated_utc = excluded.updated_utc,
                dirty = excluded.dirty,
                current_revision = excluded.current_revision
            WHERE excluded.updated_utc > local_drafts.updated_utc;
            """),
        new("pgn_documents", """
            INSERT INTO pgn_documents(
                id, source_id, title, pgn_text, metadata_json, updated_utc)
            SELECT id, source_id, title, pgn_text, metadata_json, updated_utc
            FROM incoming.pgn_documents WHERE TRUE
            ON CONFLICT(id) DO UPDATE SET
                source_id = excluded.source_id,
                title = excluded.title,
                pgn_text = excluded.pgn_text,
                metadata_json = excluded.metadata_json,
                updated_utc = excluded.updated_utc
            WHERE excluded.updated_utc > pgn_documents.updated_utc;
            """),
        new("translation_cache", """
            INSERT INTO translation_cache(
                phrase_identity, source_language, target_language, source_text,
                translated_text, status, course_id, game_id, node_id,
                server_revision, updated_utc)
            SELECT phrase_identity, source_language, target_language, source_text,
                   translated_text, status, course_id, game_id, node_id,
                   server_revision, updated_utc
            FROM incoming.translation_cache WHERE TRUE
            ON CONFLICT(phrase_identity, target_language) DO UPDATE SET
                source_language = excluded.source_language,
                source_text = excluded.source_text,
                translated_text = excluded.translated_text,
                status = excluded.status,
                course_id = excluded.course_id,
                game_id = excluded.game_id,
                node_id = excluded.node_id,
                server_revision = excluded.server_revision,
                updated_utc = excluded.updated_utc
            WHERE excluded.updated_utc > translation_cache.updated_utc;
            """),
        new("course_builder_documents", """
            INSERT INTO course_builder_documents(
                id, server_course_id, title, document_json,
                current_revision, updated_utc, dirty)
            SELECT id, server_course_id, title, document_json,
                   current_revision, updated_utc, dirty
            FROM incoming.course_builder_documents WHERE TRUE
            ON CONFLICT(id) DO UPDATE SET
                server_course_id = excluded.server_course_id,
                title = excluded.title,
                document_json = excluded.document_json,
                current_revision = excluded.current_revision,
                updated_utc = excluded.updated_utc,
                dirty = excluded.dirty
            WHERE excluded.updated_utc > course_builder_documents.updated_utc;
            """),
        new("move_trainer_courses", """
            INSERT INTO move_trainer_courses(
                id, source_id, title, settings_json, updated_utc, source_pgn)
            SELECT id, source_id, title, settings_json, updated_utc, source_pgn
            FROM incoming.move_trainer_courses WHERE TRUE
            ON CONFLICT(id) DO UPDATE SET
                source_id = excluded.source_id,
                title = excluded.title,
                settings_json = excluded.settings_json,
                updated_utc = excluded.updated_utc,
                source_pgn = excluded.source_pgn
            WHERE excluded.updated_utc > move_trainer_courses.updated_utc;
            """),
        new("course_builder_revisions", """
            INSERT OR IGNORE INTO course_builder_revisions(
                course_id, revision, document_json, reason, created_utc)
            SELECT course_id, revision, document_json, reason, created_utc
            FROM incoming.course_builder_revisions;
            """),
        new("studio_draft_revisions", """
            INSERT OR IGNORE INTO studio_draft_revisions(
                draft_id, revision, payload_json, reason, created_utc)
            SELECT draft_id, revision, payload_json, reason, created_utc
            FROM incoming.studio_draft_revisions;
            """),
        new("move_trainer_items", """
            INSERT INTO move_trainer_items(
                id, course_id, game_id, node_id, fen, answer_json,
                transposition_key, updated_utc)
            SELECT id, course_id, game_id, node_id, fen, answer_json,
                   transposition_key, updated_utc
            FROM incoming.move_trainer_items WHERE TRUE
            ON CONFLICT(id) DO UPDATE SET
                course_id = excluded.course_id,
                game_id = excluded.game_id,
                node_id = excluded.node_id,
                fen = excluded.fen,
                answer_json = excluded.answer_json,
                transposition_key = excluded.transposition_key,
                updated_utc = excluded.updated_utc
            WHERE excluded.updated_utc > move_trainer_items.updated_utc;
            """),
        new("fsrs_state", """
            INSERT INTO fsrs_state(
                user_id, item_id, due_utc, stability, difficulty, elapsed_days,
                scheduled_days, repetitions, lapses, last_review_utc, state_json)
            SELECT user_id, item_id, due_utc, stability, difficulty, elapsed_days,
                   scheduled_days, repetitions, lapses, last_review_utc, state_json
            FROM incoming.fsrs_state WHERE TRUE
            ON CONFLICT(user_id, item_id) DO UPDATE SET
                due_utc = excluded.due_utc,
                stability = excluded.stability,
                difficulty = excluded.difficulty,
                elapsed_days = excluded.elapsed_days,
                scheduled_days = excluded.scheduled_days,
                repetitions = excluded.repetitions,
                lapses = excluded.lapses,
                last_review_utc = excluded.last_review_utc,
                state_json = excluded.state_json
            WHERE COALESCE(excluded.last_review_utc, '') > COALESCE(fsrs_state.last_review_utc, '')
               OR (excluded.last_review_utc IS NULL AND fsrs_state.last_review_utc IS NULL
                   AND excluded.repetitions > fsrs_state.repetitions);
            """),
        new("practice_cards", """
            INSERT INTO practice_cards(
                id, user_id, course_id, item_id, block_id, block_type, card_key,
                prompt, fen, orientation, expected_json, source_json,
                mistake_count, success_count, soft_fail_count, fsrs_state,
                fsrs_step, stability, difficulty, retrievability, due_utc,
                last_review_utc, last_source_kind, created_utc, updated_utc)
            SELECT id, user_id, course_id, item_id, block_id, block_type, card_key,
                   prompt, fen, orientation, expected_json, source_json,
                   mistake_count, success_count, soft_fail_count, fsrs_state,
                   fsrs_step, stability, difficulty, retrievability, due_utc,
                   last_review_utc, last_source_kind, created_utc, updated_utc
            FROM incoming.practice_cards WHERE TRUE
            ON CONFLICT(id) DO UPDATE SET
                course_id = excluded.course_id,
                item_id = excluded.item_id,
                block_id = excluded.block_id,
                block_type = excluded.block_type,
                card_key = excluded.card_key,
                prompt = excluded.prompt,
                fen = excluded.fen,
                orientation = excluded.orientation,
                expected_json = excluded.expected_json,
                source_json = excluded.source_json,
                mistake_count = excluded.mistake_count,
                success_count = excluded.success_count,
                soft_fail_count = excluded.soft_fail_count,
                fsrs_state = excluded.fsrs_state,
                fsrs_step = excluded.fsrs_step,
                stability = excluded.stability,
                difficulty = excluded.difficulty,
                retrievability = excluded.retrievability,
                due_utc = excluded.due_utc,
                last_review_utc = excluded.last_review_utc,
                last_source_kind = excluded.last_source_kind,
                updated_utc = excluded.updated_utc
            WHERE excluded.updated_utc > practice_cards.updated_utc;
            """),
        new("practice_attempts", """
            INSERT OR IGNORE INTO practice_attempts(
                id, user_id, course_id, item_id, block_id, block_type,
                source_kind, attempt_kind, card_key, start_fen, result_fen,
                move_uci, move_san, selected_piece, from_square, to_square,
                input_method, hints_used, response_ms, outcome, is_correct,
                score, grade, feedback, payload_json, created_utc)
            SELECT id, user_id, course_id, item_id, block_id, block_type,
                   source_kind, attempt_kind, card_key, start_fen, result_fen,
                   move_uci, move_san, selected_piece, from_square, to_square,
                   input_method, hints_used, response_ms, outcome, is_correct,
                   score, grade, feedback, payload_json, created_utc
            FROM incoming.practice_attempts;
            """),
        new("practice_reviews", """
            INSERT OR IGNORE INTO practice_reviews(
                id, user_id, card_id, course_id, source_kind, move_uci,
                move_san, outcome, requested_rating, applied_rating,
                response_ms, fsrs_before_json, fsrs_after_json,
                review_log_json, created_utc)
            SELECT id, user_id, card_id, course_id, source_kind, move_uci,
                   move_san, outcome, requested_rating, applied_rating,
                   response_ms, fsrs_before_json, fsrs_after_json,
                   review_log_json, created_utc
            FROM incoming.practice_reviews;
            """),
        new("practice_attempt_contexts", """
            INSERT OR IGNORE INTO practice_attempt_contexts(
                attempt_id, block_snapshot_json, input_method, hints_used,
                client_data_json, created_utc)
            SELECT attempt_id, block_snapshot_json, input_method, hints_used,
                   client_data_json, created_utc
            FROM incoming.practice_attempt_contexts;
            """),
        new("move_trainer_profiles", """
            INSERT INTO move_trainer_profiles(
                user_id, course_id, profile_version, first_course_data_utc,
                last_course_data_utc, first_trainer_data_utc,
                last_trainer_data_utc, course_attempts, trainer_attempts,
                total_attempts, last_source_kind, metadata_json,
                created_utc, updated_utc)
            SELECT user_id, course_id, profile_version, first_course_data_utc,
                   last_course_data_utc, first_trainer_data_utc,
                   last_trainer_data_utc, course_attempts, trainer_attempts,
                   total_attempts, last_source_kind, metadata_json,
                   created_utc, updated_utc
            FROM incoming.move_trainer_profiles WHERE TRUE
            ON CONFLICT(user_id, course_id) DO UPDATE SET
                profile_version = excluded.profile_version,
                first_course_data_utc = excluded.first_course_data_utc,
                last_course_data_utc = excluded.last_course_data_utc,
                first_trainer_data_utc = excluded.first_trainer_data_utc,
                last_trainer_data_utc = excluded.last_trainer_data_utc,
                course_attempts = excluded.course_attempts,
                trainer_attempts = excluded.trainer_attempts,
                total_attempts = excluded.total_attempts,
                last_source_kind = excluded.last_source_kind,
                metadata_json = excluded.metadata_json,
                updated_utc = excluded.updated_utc
            WHERE excluded.updated_utc > move_trainer_profiles.updated_utc;
            """),
        new("move_trainer_sessions", """
            INSERT OR IGNORE INTO move_trainer_sessions(
                id, user_id, course_id, mode, status, current_index,
                started_utc, completed_utc, updated_utc)
            SELECT id, user_id, course_id, mode, status, current_index,
                   started_utc, completed_utc, updated_utc
            FROM incoming.move_trainer_sessions;
            """),
        new("move_trainer_session_items", """
            INSERT OR IGNORE INTO move_trainer_session_items(
                session_id, ordinal, item_id, status, attempt_count, last_outcome,
                had_mistake)
            SELECT session_id, ordinal, item_id, status, attempt_count, last_outcome,
                   had_mistake
            FROM incoming.move_trainer_session_items;
            """),
        new("move_trainer_migration_state", """
            INSERT OR IGNORE INTO move_trainer_migration_state(
                source_kind, source_id, target_id, migrated_utc)
            SELECT source_kind, source_id, target_id, migrated_utc
            FROM incoming.move_trainer_migration_state;
            """),
        new("course_runtime_current_progress", """
            INSERT INTO course_runtime_current_progress(
                course_id, user_id, attempt_id, stage_index, state_json, updated_utc)
            SELECT course_id, user_id, attempt_id, stage_index, state_json, updated_utc
            FROM incoming.course_runtime_current_progress WHERE TRUE
            ON CONFLICT(course_id, user_id) DO UPDATE SET
                attempt_id = excluded.attempt_id,
                stage_index = excluded.stage_index,
                state_json = excluded.state_json,
                updated_utc = excluded.updated_utc
            WHERE excluded.updated_utc > course_runtime_current_progress.updated_utc;
            """),
        new("course_runtime_history", """
            INSERT INTO course_runtime_history(
                attempt_id, course_id, user_id, started_utc, completed_utc, summary_json)
            SELECT attempt_id, course_id, user_id, started_utc, completed_utc, summary_json
            FROM incoming.course_runtime_history WHERE TRUE
            ON CONFLICT(attempt_id) DO UPDATE SET
                completed_utc = excluded.completed_utc,
                summary_json = excluded.summary_json
            WHERE course_runtime_history.completed_utc IS NULL
              AND excluded.completed_utc IS NOT NULL;
            """),
        new("audio_metadata", """
            INSERT INTO audio_metadata(
                id, course_id, game_id, node_id, user_id, scope, local_path,
                server_id, duration_ms, content_type, updated_utc, dirty)
            SELECT id, course_id, game_id, node_id, user_id, scope, local_path,
                   server_id, duration_ms, content_type, updated_utc, dirty
            FROM incoming.audio_metadata WHERE TRUE
            ON CONFLICT(id) DO UPDATE SET
                course_id = excluded.course_id,
                game_id = excluded.game_id,
                node_id = excluded.node_id,
                user_id = excluded.user_id,
                scope = excluded.scope,
                local_path = excluded.local_path,
                server_id = excluded.server_id,
                duration_ms = excluded.duration_ms,
                content_type = excluded.content_type,
                updated_utc = excluded.updated_utc,
                dirty = excluded.dirty
            WHERE excluded.updated_utc > audio_metadata.updated_utc;
            """),
        new("translation_cache_usages", """
            INSERT INTO translation_cache_usages(
                phrase_identity, target_language, course_id, game_id,
                node_id, comment_field, updated_utc)
            SELECT phrase_identity, target_language, course_id, game_id,
                   node_id, comment_field, updated_utc
            FROM incoming.translation_cache_usages WHERE TRUE
            ON CONFLICT(
                phrase_identity, target_language, course_id,
                game_id, node_id, comment_field) DO UPDATE SET
                updated_utc = excluded.updated_utc
            WHERE excluded.updated_utc > translation_cache_usages.updated_utc;
            """),
        new("sync_queue", """
            INSERT OR IGNORE INTO sync_queue(
                id, operation_type, entity_type, entity_id, payload_json,
                expected_revision, attempts, next_attempt_utc, last_error, created_utc)
            SELECT id, operation_type, entity_type, entity_id, payload_json,
                   expected_revision, attempts, next_attempt_utc, last_error, created_utc
            FROM incoming.sync_queue;
            """),
        new("sync_revisions", """
            INSERT INTO sync_revisions(
                entity_type, entity_id, local_revision, server_revision, synced_utc)
            SELECT entity_type, entity_id, local_revision, server_revision, synced_utc
            FROM incoming.sync_revisions WHERE TRUE
            ON CONFLICT(entity_type, entity_id) DO UPDATE SET
                local_revision = excluded.local_revision,
                server_revision = excluded.server_revision,
                synced_utc = excluded.synced_utc
            WHERE excluded.synced_utc > sync_revisions.synced_utc;
            """),
    ];

    public async Task<DatabaseUpgradeResult> ImportAsync(
        string sourcePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        var fullSourcePath = Path.GetFullPath(sourcePath);
        if (!File.Exists(fullSourcePath))
        {
            throw new FileNotFoundException("فایل دیتابیس انتخاب‌شده پیدا نشد.", fullSourcePath);
        }

        if (string.Equals(
                fullSourcePath,
                targetDatabase.DatabasePath,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            throw new DatabaseUpgradeException("فایل انتخاب‌شده همان دیتابیس فعال برنامه است.");
        }

        var temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            "ChessMentor",
            "DatabaseUpgrade",
            Guid.NewGuid().ToString("N"));
        var snapshotPath = Path.Combine(temporaryDirectory, "incoming.db");
        Directory.CreateDirectory(temporaryDirectory);

        try
        {
            var sourceVersion = await CreateValidatedSnapshotAsync(
                fullSourcePath,
                snapshotPath,
                cancellationToken).ConfigureAwait(false);

            await using (var snapshot = new AppDatabase(snapshotPath))
            {
                await snapshot.InitializeAsync(cancellationToken).ConfigureAwait(false);
            }

            return await targetDatabase.ExecuteAsync(
                connection => Merge(connection, snapshotPath, sourceVersion, cancellationToken),
                cancellationToken).ConfigureAwait(false);
        }
        catch (DatabaseUpgradeException)
        {
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new DatabaseUpgradeException(
                $"ارتقای دیتابیس انجام نشد: {exception.Message}",
                exception);
        }
        finally
        {
            TryDeleteTemporaryDirectory(temporaryDirectory);
        }
    }

    private static Task<int> CreateValidatedSnapshotAsync(
        string sourcePath,
        string snapshotPath,
        CancellationToken cancellationToken) =>
        Task.Run(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var sourceConnectionString = new SqliteConnectionStringBuilder
                {
                    DataSource = sourcePath,
                    Mode = SqliteOpenMode.ReadOnly,
                    Pooling = false,
                }.ToString();
                using var source = new SqliteConnection(sourceConnectionString);
                source.Open();

                using (var check = source.CreateCommand())
                {
                    check.CommandText = "PRAGMA quick_check;";
                    if (!string.Equals(check.ExecuteScalar() as string, "ok", StringComparison.OrdinalIgnoreCase))
                    {
                        throw new DatabaseUpgradeException("فایل SQLite سالم نیست یا بررسی یکپارچگی آن شکست خورد.");
                    }
                }

                foreach (var requiredTable in new[]
                         {
                             "schema_migrations", "settings", "local_drafts", "pgn_documents",
                         })
                {
                    if (!HasTable(source, requiredTable))
                    {
                        throw new DatabaseUpgradeException(
                            "ساختار فایل با دیتابیس نسخه Native ChessMentor منطبق نیست.");
                    }
                }

                using var versionCommand = source.CreateCommand();
                versionCommand.CommandText = "SELECT MAX(version) FROM schema_migrations;";
                var value = versionCommand.ExecuteScalar();
                if (value is null || value is DBNull)
                {
                    throw new DatabaseUpgradeException("نسخهٔ Schema دیتابیس انتخاب‌شده مشخص نیست.");
                }

                var version = Convert.ToInt32(value, CultureInfo.InvariantCulture);
                if (version < 1)
                {
                    throw new DatabaseUpgradeException("نسخهٔ Schema دیتابیس انتخاب‌شده معتبر نیست.");
                }

                if (version > DatabaseMigrator.CurrentVersion)
                {
                    throw new DatabaseUpgradeException(
                        "این دیتابیس با نسخهٔ جدیدتری از ChessMentor ساخته شده است؛ ابتدا خود برنامه را به‌روز کنید.");
                }

                var snapshotConnectionString = new SqliteConnectionStringBuilder
                {
                    DataSource = snapshotPath,
                    Mode = SqliteOpenMode.ReadWriteCreate,
                    Pooling = false,
                }.ToString();
                using var destination = new SqliteConnection(snapshotConnectionString);
                destination.Open();
                source.BackupDatabase(destination);
                return version;
            },
            cancellationToken);

    private static DatabaseUpgradeResult Merge(
        SqliteConnection connection,
        string snapshotPath,
        int sourceVersion,
        CancellationToken cancellationToken)
    {
        Attach(connection, snapshotPath);
        try
        {
            ValidateMigratedSchema(connection);
            var tableResults = new List<DatabaseUpgradeTableResult>(ImportPlans.Count);
            using var transaction = connection.BeginTransaction();
            foreach (var plan in ImportPlans)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var sourceRows = CountRows(connection, transaction, plan.Table);
                using var import = connection.CreateCommand();
                import.Transaction = transaction;
                import.CommandText = plan.Sql;
                import.ExecuteNonQuery();
                using var changes = connection.CreateCommand();
                changes.Transaction = transaction;
                changes.CommandText = "SELECT changes();";
                var changedRows = Convert.ToInt64(changes.ExecuteScalar(), CultureInfo.InvariantCulture);
                tableResults.Add(new DatabaseUpgradeTableResult(plan.Table, sourceRows, changedRows));
            }

            transaction.Commit();
            return new DatabaseUpgradeResult(sourceVersion, tableResults);
        }
        finally
        {
            Detach(connection);
        }
    }

    private static void ValidateMigratedSchema(SqliteConnection connection)
    {
        foreach (var table in ImportPlans.Select(static plan => plan.Table))
        {
            var targetColumns = ReadColumns(connection, "main", table);
            var sourceColumns = ReadColumns(connection, "incoming", table);
            if (targetColumns.Count == 0 || !targetColumns.SequenceEqual(sourceColumns))
            {
                throw new DatabaseUpgradeException(
                    $"ساختار جدول «{table}» با دیتابیس فعال برنامه منطبق نیست.");
            }
        }
    }

    private static IReadOnlyList<ColumnSignature> ReadColumns(
        SqliteConnection connection,
        string databaseAlias,
        string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA {databaseAlias}.table_info(\"{table}\");";
        using var reader = command.ExecuteReader();
        var result = new List<ColumnSignature>();
        while (reader.Read())
        {
            result.Add(new ColumnSignature(
                reader.GetInt32(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt32(3) != 0,
                reader.GetInt32(5)));
        }

        return result;
    }

    private static bool HasTable(SqliteConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $name);";
        command.Parameters.AddWithValue("$name", table);
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture) == 1;
    }

    private static long CountRows(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string table)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT COUNT(*) FROM incoming.\"{table}\";";
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static void Attach(SqliteConnection connection, string snapshotPath)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "ATTACH DATABASE $path AS incoming;";
        command.Parameters.AddWithValue("$path", snapshotPath);
        command.ExecuteNonQuery();
    }

    private static void Detach(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "DETACH DATABASE incoming;";
        command.ExecuteNonQuery();
    }

    private static void TryDeleteTemporaryDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed record ImportPlan(string Table, string Sql);

    private sealed record ColumnSignature(
        int Ordinal,
        string Name,
        string Type,
        bool NotNull,
        int PrimaryKeyOrdinal);
}
