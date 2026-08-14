using ChessMentor.Chess;
using ChessMentor.Persistence;
using Microsoft.Data.Sqlite;

namespace ChessMentor.Tests;

public sealed class PersistenceTests
{
    [Fact]
    public async Task MigrationCreatesEveryPhaseOneDomainTable()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var path = TemporaryDatabasePath();
        await using var database = new AppDatabase(path);
        await database.InitializeAsync(cancellationToken);

        var tables = await database.ExecuteAsync(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name;";
            using var reader = command.ExecuteReader();
            var result = new HashSet<string>(StringComparer.Ordinal);
            while (reader.Read())
            {
                result.Add(reader.GetString(0));
            }

            return result;
        }, cancellationToken);

        Assert.Contains("settings", tables);
        Assert.Contains("local_drafts", tables);
        Assert.Contains("pgn_documents", tables);
        Assert.Contains("translation_cache", tables);
        Assert.Contains("translation_cache_usages", tables);
        Assert.Contains("course_builder_documents", tables);
        Assert.Contains("course_builder_revisions", tables);
        Assert.Contains("course_runtime_current_progress", tables);
        Assert.Contains("course_runtime_history", tables);
        Assert.Contains("move_trainer_courses", tables);
        Assert.Contains("move_trainer_items", tables);
        Assert.Contains("fsrs_state", tables);
        Assert.Contains("audio_metadata", tables);
        Assert.Contains("sync_queue", tables);
        Assert.Contains("sync_revisions", tables);
        Assert.Contains("studio_draft_revisions", tables);
    }

    [Fact]
    public async Task GlobalBoardSkinAndDisplaySettingsSurviveReopen()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var path = TemporaryDatabasePath();
        await using (var firstDatabase = new AppDatabase(path))
        {
            await firstDatabase.InitializeAsync(cancellationToken);
            var repository = new SettingsRepository(firstDatabase);
            await repository.SaveAsync(
                new AppSettings(
                    BoardSkin.Murphy,
                    ShowCoordinates: false,
                    HeaderCollapsed: true,
                    GamesPanelCollapsed: true,
                    ViewerMoveDisplayMode: "Mobile",
                    ViewerNotationMode: "Figurines",
                    MoveSoundEnabled: false,
                    GamesPanelWidth: 325,
                    MovesPanelWidth: 410,
                    CommentFontSize: 18,
                    CommentFontFamilyName: "Test Persian Font",
                    CustomCommentFontPath: @"C:\Fonts\test.ttf",
                    LocalInstallationId: "device:test",
                    StudioMovesPanelWidth: 455,
                    StudioGamesPanelWidth: 315),
                cancellationToken);
        }

        await using var secondDatabase = new AppDatabase(path);
        await secondDatabase.InitializeAsync(cancellationToken);
        var reloaded = await new SettingsRepository(secondDatabase).LoadAsync(cancellationToken);

        Assert.Equal(BoardSkin.Murphy, reloaded.BoardSkin);
        Assert.False(reloaded.ShowCoordinates);
        Assert.True(reloaded.HeaderCollapsed);
        Assert.True(reloaded.GamesPanelCollapsed);
        Assert.Equal("Mobile", reloaded.ViewerMoveDisplayMode);
        Assert.Equal("Figurines", reloaded.ViewerNotationMode);
        Assert.False(reloaded.MoveSoundEnabled);
        Assert.Equal(325, reloaded.GamesPanelWidth);
        Assert.Equal(410, reloaded.MovesPanelWidth);
        Assert.Equal(18, reloaded.CommentFontSize);
        Assert.Equal("Test Persian Font", reloaded.CommentFontFamilyName);
        Assert.Equal(@"C:\Fonts\test.ttf", reloaded.CustomCommentFontPath);
        Assert.Equal("device:test", reloaded.LocalInstallationId);
        Assert.Equal(455, reloaded.StudioMovesPanelWidth);
        Assert.Equal(315, reloaded.StudioGamesPanelWidth);
    }

    [Fact]
    public async Task StudioDraftRevisionsSurviveReopenWithoutOverwritingHistory()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var path = TemporaryDatabasePath();
        await using (var firstDatabase = new AppDatabase(path))
        {
            await firstDatabase.InitializeAsync(cancellationToken);
            var drafts = new LocalDraftRepository(firstDatabase);
            _ = await drafts.SaveAsync(
                "draft-1",
                "source-1",
                "اول",
                "{\"revision\":1}",
                "import",
                cancellationToken: cancellationToken);
            _ = await drafts.SaveAsync(
                "draft-1",
                "source-1",
                "دوم",
                "{\"revision\":2}",
                "autosave",
                cancellationToken: cancellationToken);
        }

        await using var secondDatabase = new AppDatabase(path);
        await secondDatabase.InitializeAsync(cancellationToken);
        var reopened = new LocalDraftRepository(secondDatabase);
        var current = await reopened.GetAsync("draft-1", cancellationToken);
        var summaries = await reopened.ListSummariesAsync(cancellationToken: cancellationToken);
        var revisions = await reopened.RevisionsAsync("draft-1", cancellationToken: cancellationToken);

        Assert.NotNull(current);
        Assert.Equal(2, current.CurrentRevision);
        Assert.Equal("{\"revision\":2}", current.PayloadJson);
        Assert.Single(summaries);
        Assert.Empty(summaries[0].PayloadJson);
        Assert.Equal("دوم", summaries[0].Title);
        Assert.Equal([2, 1], revisions.Select(static revision => revision.Revision));
        Assert.Equal(["autosave", "import"], revisions.Select(static revision => revision.Reason));
    }

    [Fact]
    public async Task TranslationCacheAndSyncQueueUseBatchedPersistentStorage()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var path = TemporaryDatabasePath();
        await using var database = new AppDatabase(path);
        await database.InitializeAsync(cancellationToken);
        var cache = new TranslationCacheRepository(database);
        var entries = Enumerable.Range(0, 425).Select(index => new TranslationCacheEntry(
            $"{index:x64}",
            "en",
            "fa",
            $"source {index}",
            $"ترجمه {index}",
            "server",
            null,
            "game",
            $"node-{index}",
            null,
            DateTimeOffset.UtcNow)).ToArray();
        await cache.UpsertManyAsync(entries, cancellationToken);

        var found = await cache.GetManyAsync(
            entries.Select(static entry => entry.PhraseIdentity).ToArray(),
            "fa",
            cancellationToken);
        Assert.Equal(entries.Length, found.Count);
        Assert.Equal("ترجمه 424", found[entries[^1].PhraseIdentity].TranslatedText);

        var sync = new SyncQueueRepository(database);
        await sync.EnqueueAsync("draft:1", "studio-draft-save", "draft", "1", "{}", cancellationToken: cancellationToken);
        Assert.True(await sync.ContainsAsync("draft:1", cancellationToken));
        await sync.EnqueueAsync("draft:1", "studio-draft-save", "draft", "1", "{\"new\":true}", cancellationToken: cancellationToken);
        var pending = Assert.Single(await sync.ReadyAsync(cancellationToken: cancellationToken));
        Assert.Equal("{\"new\":true}", pending.PayloadJson);
        Assert.Equal(0, pending.Attempts);
        await sync.CompleteAsync(pending.Id, cancellationToken);
        Assert.Equal(0, await sync.CountAsync(cancellationToken));
    }

    [Fact]
    public async Task AudioMetadataKeepsCourseGameNodeAndScopeSeparate()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var path = TemporaryDatabasePath();
        await using var database = new AppDatabase(path);
        await database.InitializeAsync(cancellationToken);
        var audio = new AudioMetadataRepository(database);
        var publicAudio = new AudioMetadataRecord(
            "audio-course",
            "draft-1",
            "game-1",
            "node-1",
            null,
            "course",
            "course.wav",
            "41",
            1100,
            "audio/wav",
            DateTimeOffset.UtcNow,
            Dirty: false);
        var personalAudio = publicAudio with
        {
            Id = "audio-user",
            Scope = "user",
            UserId = "user-7",
            LocalPath = "user.wav",
            ServerId = "42",
            Dirty = true,
        };
        await audio.UpsertAsync(publicAudio, cancellationToken);
        await audio.UpsertAsync(personalAudio, cancellationToken);

        var records = await audio.ListForNodeAsync("draft-1", "game-1", "node-1", cancellationToken);

        Assert.Equal(2, records.Count);
        Assert.Contains(records, record => record.Scope == "course" && record.ServerId == "41");
        Assert.Contains(records, record => record.Scope == "user" && record.UserId == "user-7" && record.Dirty);
        Assert.Equal("audio-course", (await audio.FindByServerIdAsync("draft-1", "41", cancellationToken))?.Id);
    }

    [Fact]
    public async Task DatabaseUpgradeMergesEveryCompatibleDomainAtomically()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var sourcePath = TemporaryDatabasePath();
        var targetPath = TemporaryDatabasePath();
        await using (var source = new AppDatabase(sourcePath))
        {
            await source.InitializeAsync(cancellationToken);
            await source.ExecuteAsync(
                connection =>
                {
                    using var command = connection.CreateCommand();
                    command.CommandText = """
                        INSERT INTO settings VALUES('legacy', '{}', '2026-01-01T00:00:00.0000000+00:00');
                        INSERT INTO local_drafts VALUES(
                            'draft-1', 'source-1', 'draft', '{}', NULL,
                            '2026-01-01T00:00:00.0000000+00:00', 1, 1);
                        INSERT INTO studio_draft_revisions VALUES(
                            'draft-1', 1, '{}', 'import', '2026-01-01T00:00:00.0000000+00:00');
                        INSERT INTO pgn_documents VALUES(
                            'pgn-1', 'source-1', 'source title', '1. e4 *', '{}',
                            '2026-01-01T00:00:00.0000000+00:00');
                        INSERT INTO translation_cache VALUES(
                            'phrase-1', 'en', 'fa', 'source', 'ترجمه', 'server',
                            'course-1', 'game-1', 'node-1', 'rev-1',
                            '2026-01-01T00:00:00.0000000+00:00');
                        INSERT INTO translation_cache_usages VALUES(
                            'phrase-1', 'fa', 'course-1', 'game-1', 'node-1', 'comment',
                            '2026-01-01T00:00:00.0000000+00:00');
                        INSERT INTO course_builder_documents VALUES(
                            'course-1', NULL, 'course', '{}', 1,
                            '2026-01-01T00:00:00.0000000+00:00', 1);
                        INSERT INTO course_builder_revisions VALUES(
                            'course-1', 1, '{}', 'checkpoint',
                            '2026-01-01T00:00:00.0000000+00:00');
                        INSERT INTO course_runtime_current_progress VALUES(
                            'course-1', 'user-1', 'attempt-1', 2, '{}',
                            '2026-01-01T00:00:00.0000000+00:00');
                        INSERT INTO course_runtime_history VALUES(
                            'attempt-1', 'course-1', 'user-1',
                            '2026-01-01T00:00:00.0000000+00:00',
                            '2026-01-01T00:05:00.0000000+00:00', '{}');
                        INSERT INTO move_trainer_courses VALUES(
                            'trainer-1', 'source-1', 'trainer', '{}',
                            '2026-01-01T00:00:00.0000000+00:00');
                        INSERT INTO move_trainer_items VALUES(
                            'item-1', 'trainer-1', 'game-1', 'node-1',
                            '8/8/8/8/8/8/8/K6k w - - 0 1', '{}', 'position-1',
                            '2026-01-01T00:00:00.0000000+00:00');
                        INSERT INTO fsrs_state VALUES(
                            'user-1', 'item-1', '2026-01-02T00:00:00.0000000+00:00',
                            1.0, 5.0, 0, 1, 1, 0,
                            '2026-01-01T00:00:00.0000000+00:00', '{}');
                        INSERT INTO audio_metadata VALUES(
                            'audio-1', 'course-1', 'game-1', 'node-1', NULL, 'course',
                            'audio.wav', NULL, 900, 'audio/wav',
                            '2026-01-01T00:00:00.0000000+00:00', 1);
                        INSERT INTO sync_queue VALUES(
                            'sync-1', 'save', 'draft', 'draft-1', '{}', NULL, 0,
                            '2026-01-01T00:00:00.0000000+00:00', NULL,
                            '2026-01-01T00:00:00.0000000+00:00');
                        INSERT INTO sync_revisions VALUES(
                            'draft', 'draft-1', '1', '1',
                            '2026-01-01T00:00:00.0000000+00:00');
                        """;
                    command.ExecuteNonQuery();
                    return 0;
                },
                cancellationToken);
        }

        await using var target = new AppDatabase(targetPath);
        await target.InitializeAsync(cancellationToken);
        await target.ExecuteAsync(
            connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = """
                    INSERT INTO pgn_documents VALUES(
                        'pgn-1', 'target-source', 'newer target', '1. d4 *', '{}',
                        '2027-01-01T00:00:00.0000000+00:00');
                    """;
                command.ExecuteNonQuery();
                return 0;
            },
            cancellationToken);

        var result = await new DatabaseUpgradeService(target).ImportAsync(sourcePath, cancellationToken);

        Assert.Equal(3, result.SourceSchemaVersion);
        Assert.Equal(16L, result.SourceRows);
        Assert.Equal(15L, result.ImportedOrUpdatedRows);
        var actual = await target.ExecuteAsync(
            connection =>
            {
                var counts = new Dictionary<string, long>(StringComparer.Ordinal);
                foreach (var table in result.Tables.Select(static item => item.Table))
                {
                    using var command = connection.CreateCommand();
                    command.CommandText = $"SELECT COUNT(*) FROM \"{table}\";";
                    counts[table] = (long)(command.ExecuteScalar() ?? 0L);
                }

                using var pgn = connection.CreateCommand();
                pgn.CommandText = "SELECT title, pgn_text FROM pgn_documents WHERE id = 'pgn-1';";
                using var reader = pgn.ExecuteReader();
                Assert.True(reader.Read());
                return (Counts: counts, Title: reader.GetString(0), Pgn: reader.GetString(1));
            },
            cancellationToken);

        Assert.All(actual.Counts.Values, static count => Assert.Equal(1L, count));
        Assert.Equal("newer target", actual.Title);
        Assert.Equal("1. d4 *", actual.Pgn);
    }

    [Fact]
    public async Task DatabaseUpgradeRejectsUnrelatedSqliteWithoutChangingTarget()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var unrelatedPath = TemporaryDatabasePath();
        Directory.CreateDirectory(Path.GetDirectoryName(unrelatedPath)!);
        using (var unrelated = new SqliteConnection($"Data Source={unrelatedPath}"))
        {
            unrelated.Open();
            using var command = unrelated.CreateCommand();
            command.CommandText = "CREATE TABLE unrelated(id INTEGER PRIMARY KEY); INSERT INTO unrelated VALUES(1);";
            command.ExecuteNonQuery();
        }

        var targetPath = TemporaryDatabasePath();
        await using var target = new AppDatabase(targetPath);
        await target.InitializeAsync(cancellationToken);

        var exception = await Assert.ThrowsAsync<DatabaseUpgradeException>(
            () => new DatabaseUpgradeService(target).ImportAsync(unrelatedPath, cancellationToken));

        Assert.Contains("منطبق نیست", exception.Message);
        Assert.Empty(await new LocalDraftRepository(target).ListAsync(cancellationToken));
    }

    private static string TemporaryDatabasePath()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ChessMentor.Tests", Guid.NewGuid().ToString("N"));
        return Path.Combine(directory, "test.db");
    }
}
