using ChessMentor.Chess;
using ChessMentor.MoveTrainer;
using ChessMentor.Persistence;
using Microsoft.Data.Sqlite;

namespace ChessMentor.Tests;

public sealed class MoveTrainerPersistenceTests
{
    [Fact]
    public async Task CourseAttemptsFsrsQueueAndStatsSurviveDatabaseReopen()
    {
        var token = TestContext.Current.CancellationToken;
        var path = TemporaryDatabasePath();
        var now = DateTimeOffset.Parse("2030-01-02T12:00:00Z");
        var item = Item();
        var course = new TrainerCourse(
            "course-1",
            "تمرین تست",
            [item],
            new TrainerCourseSettings(DailyNewLimit: 5, DailyReviewLimit: 10),
            "1. e4 *",
            now);

        await using (var database = new AppDatabase(path))
        {
            await database.InitializeAsync(token);
            var repository = new MoveTrainerRepository(database);
            await repository.SaveCourseAsync(course, token);
            var loaded = await repository.GetCourseAsync(course.Id, token);
            Assert.NotNull(loaded);
            Assert.Equal(course.SourcePgn, loaded.SourcePgn);
            Assert.Equal(item.Answers, Assert.Single(loaded.Items).Answers);

            var evaluator = new TrainerAnswerEvaluator();
            var wrongRequest = new TrainerAttemptRequest(
                "d2d4",
                TrainerInputMethod.Drag,
                SelectedPiece: "P",
                FromSquare: "d2",
                ToSquare: "d4",
                ResponseMilliseconds: 900);
            var wrong = evaluator.Evaluate(item, wrongRequest, acceptTranspositions: false, token);
            var first = await repository.RecordAttemptAsync(
                "user-1", course, item, wrongRequest, wrong, now, cancellationToken: token);
            Assert.True(first.WasNewCard);
            Assert.Equal(TrainerRating.Again, first.Fsrs.AppliedRating);

            var correctRequest = new TrainerAttemptRequest(
                "e2e4",
                TrainerInputMethod.Click,
                ResponseMilliseconds: 500);
            var correct = evaluator.Evaluate(item, correctRequest, acceptTranspositions: true, token);
            var second = await repository.RecordAttemptAsync(
                "user-1", course, item, correctRequest, correct, now.AddMinutes(1), cancellationToken: token);
            Assert.False(second.WasNewCard);
            Assert.Equal(first.CardId, second.CardId);

            var session = new MoveTrainerSession([item], course.Settings, evaluator, "session-1");
            session.Submit(wrongRequest, token);
            await repository.SaveSessionAsync(
                "user-1", course.Id, session.Snapshot(), now.AddMinutes(2), token);
            var resumable = await repository.GetLatestActiveSessionAsync("user-1", course.Id, token);
            Assert.NotNull(resumable);
            Assert.Equal("session-1", resumable.SessionId);
            Assert.Equal(item.Id, Assert.Single(resumable.Items).ItemId);
            Assert.Contains(item.Id, resumable.MistakeItemIds);

            session.Submit(correctRequest, token);
            await repository.SaveSessionAsync(
                "user-1", course.Id, session.Snapshot(), now.AddMinutes(3), token);
            Assert.Null(await repository.GetLatestActiveSessionAsync("user-1", course.Id, token));

            await repository.SaveCourseAsync(
                course with { Title = "عنوان ویرایش‌شده", UpdatedUtc = now.AddMinutes(4) },
                token);

            var stats = await repository.GetStatsAsync("user-1", course.Id, now.AddMinutes(2), token);
            Assert.Equal(2, stats.Attempts);
            Assert.Equal(1, stats.Correct);
            Assert.Equal(1, stats.Mistakes);
            Assert.Equal(1, stats.Cards);
            Assert.Equal(50d, stats.Accuracy);
            Assert.Equal(50d, stats.AcceptedAccuracy);

            var counts = await database.ExecuteAsync(
                connection =>
                {
                    static long Count(SqliteConnection db, string table)
                    {
                        using var command = db.CreateCommand();
                        command.CommandText = $"SELECT COUNT(*) FROM {table};";
                        return (long)(command.ExecuteScalar() ?? 0L);
                    }

                    return new
                    {
                        Attempts = Count(connection, "practice_attempts"),
                        Reviews = Count(connection, "practice_reviews"),
                        Contexts = Count(connection, "practice_attempt_contexts"),
                        Profiles = Count(connection, "move_trainer_profiles"),
                        Legacy = Count(connection, "fsrs_state"),
                        Sessions = Count(connection, "move_trainer_sessions"),
                        SessionItems = Count(connection, "move_trainer_session_items"),
                    };
                },
                token);
            Assert.Equal(2, counts.Attempts);
            Assert.Equal(2, counts.Reviews);
            Assert.Equal(2, counts.Contexts);
            Assert.Equal(1, counts.Profiles);
            Assert.Equal(1, counts.Legacy);
            Assert.Equal(1, counts.Sessions);
            Assert.Equal(1, counts.SessionItems);
        }

        await using (var reopened = new AppDatabase(path))
        {
            await reopened.InitializeAsync(token);
            var repository = new MoveTrainerRepository(reopened);
            var loaded = await repository.GetCourseAsync(course.Id, token);
            Assert.NotNull(loaded);
            var stats = await repository.GetStatsAsync("user-1", course.Id, now.AddMinutes(3), token);
            Assert.Equal(2, stats.Attempts);
            Assert.Equal(1, stats.Cards);
        }
    }

    [Fact]
    public async Task VersionThreeFsrsRowsMigrateWithoutResetOrDeletion()
    {
        var token = TestContext.Current.CancellationToken;
        var path = TemporaryDatabasePath();
        var connectionString = new SqliteConnectionStringBuilder { DataSource = path }.ToString();
        using (var connection = new SqliteConnection(connectionString))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE schema_migrations(version INTEGER PRIMARY KEY, applied_utc TEXT NOT NULL);
                INSERT INTO schema_migrations VALUES(1, '2026-01-01T00:00:00Z');
                INSERT INTO schema_migrations VALUES(2, '2026-01-01T00:00:00Z');
                INSERT INTO schema_migrations VALUES(3, '2026-01-01T00:00:00Z');
                CREATE TABLE move_trainer_courses(
                    id TEXT PRIMARY KEY, source_id TEXT, title TEXT NOT NULL DEFAULT '',
                    settings_json TEXT NOT NULL DEFAULT '{}', updated_utc TEXT NOT NULL);
                CREATE TABLE move_trainer_items(
                    id TEXT PRIMARY KEY, course_id TEXT NOT NULL, game_id TEXT, node_id TEXT,
                    fen TEXT NOT NULL, answer_json TEXT NOT NULL, transposition_key TEXT,
                    updated_utc TEXT NOT NULL,
                    FOREIGN KEY(course_id) REFERENCES move_trainer_courses(id) ON DELETE CASCADE);
                CREATE TABLE fsrs_state(
                    user_id TEXT NOT NULL, item_id TEXT NOT NULL, due_utc TEXT NOT NULL,
                    stability REAL NOT NULL, difficulty REAL NOT NULL,
                    elapsed_days INTEGER NOT NULL, scheduled_days INTEGER NOT NULL,
                    repetitions INTEGER NOT NULL, lapses INTEGER NOT NULL,
                    last_review_utc TEXT, state_json TEXT NOT NULL DEFAULT '{}',
                    PRIMARY KEY(user_id,item_id),
                    FOREIGN KEY(item_id) REFERENCES move_trainer_items(id) ON DELETE CASCADE);
                INSERT INTO move_trainer_courses VALUES(
                    'legacy-course', NULL, 'Legacy', '{}', '2026-01-01T00:00:00Z');
                INSERT INTO move_trainer_items VALUES(
                    'legacy-item', 'legacy-course', 'game-1', 'node-1',
                    '8/8/8/8/8/8/8/K6k w - - 0 1', '[]', 'position-1',
                    '2026-01-01T00:00:00Z');
                INSERT INTO fsrs_state VALUES(
                    'legacy-user', 'legacy-item', '2026-02-01T00:00:00Z',
                    12.5, 7.25, 5, 20, 11, 3,
                    '2026-01-12T00:00:00Z', '{"preserve":true}');
                """;
            command.ExecuteNonQuery();
        }

        await using var database = new AppDatabase(path);
        await database.InitializeAsync(token);
        var migrated = await database.ExecuteAsync(
            connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = """
                    SELECT pc.stability, pc.difficulty, pc.mistake_count,
                           pc.due_utc,
                           (SELECT COUNT(*) FROM fsrs_state),
                           (SELECT COUNT(*) FROM move_trainer_migration_state)
                    FROM practice_cards pc
                    WHERE pc.user_id = 'legacy-user' AND pc.item_id = 'legacy-item';
                    """;
                using var reader = command.ExecuteReader();
                Assert.True(reader.Read());
                return new
                {
                    Stability = reader.GetDouble(0),
                    Difficulty = reader.GetDouble(1),
                    Mistakes = reader.GetInt32(2),
                    Due = reader.GetString(3),
                    LegacyRows = reader.GetInt64(4),
                    MigrationRows = reader.GetInt64(5),
                };
            },
            token);

        Assert.Equal(12.5, migrated.Stability);
        Assert.Equal(7.25, migrated.Difficulty);
        Assert.Equal(3, migrated.Mistakes);
        Assert.Equal("2026-02-01T00:00:00Z", migrated.Due);
        Assert.Equal(1, migrated.LegacyRows);
        Assert.Equal(1, migrated.MigrationRows);
    }

    private static TrainerItem Item() =>
        new(
            "item-1",
            "course-1",
            "game-1",
            "node-1",
            FenPosition.Initial,
            ManagedChessRules.PositionKey(FenPosition.Initial),
            [new TrainerAnswer("e2e4", "e4", TrainerAnswerKind.Primary)],
            [new TrainerHint("text", "مرکز را بگیرید.", 10)],
            "بهترین حرکت سفید چیست؟",
            "دوباره تلاش کنید.");

    private static string TemporaryDatabasePath()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ChessMentor.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, "move-trainer.db");
    }
}
