using System.Text.Json;
using System.Text.Json.Serialization;
using ChessMentor.Core;
using ChessMentor.Persistence;
using Microsoft.Data.Sqlite;

namespace ChessMentor.MoveTrainer;

public sealed record RecordedPracticeAttempt(
    string AttemptId,
    string CardId,
    TrainerEvaluation Evaluation,
    FsrsReviewResult Fsrs,
    bool WasNewCard);

public sealed record MoveTrainerStats(
    int Attempts,
    int Correct,
    int SoftFails,
    int Mistakes,
    int Cards,
    int Due,
    double Accuracy,
    double AcceptedAccuracy,
    double AverageDifficulty,
    double AverageRetrievability);

public sealed class MoveTrainerRepository
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
    private readonly AppDatabase _database;
    private readonly FsrsScheduler _scheduler;
    private readonly TrainerQueuePlanner _queuePlanner;

    public MoveTrainerRepository(
        AppDatabase database,
        FsrsScheduler? scheduler = null,
        TrainerQueuePlanner? queuePlanner = null)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _scheduler = scheduler ?? new FsrsScheduler();
        _queuePlanner = queuePlanner ?? new TrainerQueuePlanner();
    }

    public Task SaveCourseAsync(TrainerCourse course, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(course);
        ArgumentException.ThrowIfNullOrWhiteSpace(course.Id);
        if (course.Items.GroupBy(static item => item.Id, StringComparer.Ordinal).Any(static group => group.Count() > 1))
        {
            throw new InvalidDataException("MoveTrainer course contains duplicate item IDs.");
        }

        return _database.ExecuteAsync(
            connection =>
            {
                using var transaction = connection.BeginTransaction();
                using (var command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText = """
                        INSERT INTO move_trainer_courses(
                            id, source_id, title, settings_json, updated_utc, source_pgn)
                        VALUES($id, $source, $title, $settings, $updated, $pgn)
                        ON CONFLICT(id) DO UPDATE SET
                            source_id = excluded.source_id,
                            title = excluded.title,
                            settings_json = excluded.settings_json,
                            updated_utc = excluded.updated_utc,
                            source_pgn = excluded.source_pgn;
                        """;
                    command.Parameters.AddWithValue("$id", course.Id);
                    command.Parameters.AddWithValue("$source", DBNull.Value);
                    command.Parameters.AddWithValue("$title", course.Title);
                    command.Parameters.AddWithValue("$settings", JsonSerializer.Serialize(course.Settings.Normalize(), JsonOptions));
                    command.Parameters.AddWithValue("$updated", course.UpdatedUtc.ToString("O"));
                    command.Parameters.AddWithValue("$pgn", course.SourcePgn);
                    command.ExecuteNonQuery();
                }

                var retained = new HashSet<string>(StringComparer.Ordinal);
                foreach (var item in course.Items)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!string.Equals(item.CourseId, course.Id, StringComparison.Ordinal))
                    {
                        throw new InvalidDataException("MoveTrainer item belongs to a different course.");
                    }

                    retained.Add(item.Id);
                    var payload = new PersistedTrainerItem(
                        item.Answers,
                        item.Hints,
                        item.Prompt,
                        item.WrongMoveFeedback,
                        item.Priority,
                        item.Enabled,
                        item.PositionKey);
                    using var command = connection.CreateCommand();
                    command.Transaction = transaction;
                    command.CommandText = """
                        INSERT INTO move_trainer_items(
                            id, course_id, game_id, node_id, fen, answer_json,
                            transposition_key, updated_utc)
                        VALUES($id, $course, $game, $node, $fen, $payload, $position, $updated)
                        ON CONFLICT(id) DO UPDATE SET
                            course_id = excluded.course_id,
                            game_id = excluded.game_id,
                            node_id = excluded.node_id,
                            fen = excluded.fen,
                            answer_json = excluded.answer_json,
                            transposition_key = excluded.transposition_key,
                            updated_utc = excluded.updated_utc;
                        """;
                    command.Parameters.AddWithValue("$id", item.Id);
                    command.Parameters.AddWithValue("$course", item.CourseId);
                    command.Parameters.AddWithValue("$game", item.GameId);
                    command.Parameters.AddWithValue("$node", item.NodeId);
                    command.Parameters.AddWithValue("$fen", item.Fen);
                    command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(payload, JsonOptions));
                    command.Parameters.AddWithValue("$position", item.PositionKey);
                    command.Parameters.AddWithValue("$updated", course.UpdatedUtc.ToString("O"));
                    command.ExecuteNonQuery();
                }

                var existing = new List<string>();
                using (var query = connection.CreateCommand())
                {
                    query.Transaction = transaction;
                    query.CommandText = "SELECT id FROM move_trainer_items WHERE course_id = $course;";
                    query.Parameters.AddWithValue("$course", course.Id);
                    using var reader = query.ExecuteReader();
                    while (reader.Read())
                    {
                        existing.Add(reader.GetString(0));
                    }
                }

                foreach (var removed in existing.Where(id => !retained.Contains(id)))
                {
                    using var delete = connection.CreateCommand();
                    delete.Transaction = transaction;
                    delete.CommandText = "DELETE FROM move_trainer_items WHERE id = $id;";
                    delete.Parameters.AddWithValue("$id", removed);
                    delete.ExecuteNonQuery();
                }

                transaction.Commit();
                return 0;
            },
            cancellationToken);
    }

    public Task<IReadOnlyList<TrainerCourse>> ListCoursesAsync(CancellationToken cancellationToken = default) =>
        _database.ExecuteAsync<IReadOnlyList<TrainerCourse>>(
            connection =>
            {
                var courses = new List<TrainerCourse>();
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT id FROM move_trainer_courses ORDER BY updated_utc DESC, id;";
                using var reader = command.ExecuteReader();
                var ids = new List<string>();
                while (reader.Read())
                {
                    ids.Add(reader.GetString(0));
                }

                foreach (var id in ids)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var course = LoadCourse(connection, id);
                    if (course is not null)
                    {
                        courses.Add(course);
                    }
                }

                return courses;
            },
            cancellationToken);

    public Task<TrainerCourse?> GetCourseAsync(
        string courseId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(courseId);
        return _database.ExecuteAsync(
            connection => LoadCourse(connection, courseId),
            cancellationToken);
    }

    public async Task<TrainerQueuePlan> BuildQueueAsync(
        string userId,
        TrainerCourse course,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ArgumentNullException.ThrowIfNull(course);
        var data = await _database.ExecuteAsync(
            connection => ReadQueueData(connection, userId, course, now),
            cancellationToken).ConfigureAwait(false);
        return _queuePlanner.Build(
            data.Candidates,
            course.Settings,
            now,
            data.NewToday,
            data.ReviewsToday);
    }

    public Task<RecordedPracticeAttempt> RecordAttemptAsync(
        string userId,
        TrainerCourse course,
        TrainerItem item,
        TrainerAttemptRequest request,
        TrainerEvaluation evaluation,
        DateTimeOffset reviewedAt,
        string sourceKind = "move_trainer",
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ArgumentNullException.ThrowIfNull(course);
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(evaluation);
        if (sourceKind is not ("move_trainer" or "course_runtime"))
        {
            throw new ArgumentOutOfRangeException(nameof(sourceKind));
        }

        return _database.ExecuteAsync(
            connection => RecordAttempt(
                connection,
                userId,
                course,
                item,
                request,
                evaluation,
                reviewedAt,
                sourceKind,
                cancellationToken),
            cancellationToken);
    }

    public Task<MoveTrainerStats> GetStatsAsync(
        string userId,
        string? courseId,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        return _database.ExecuteAsync(
            connection => ReadStats(connection, userId, courseId, now),
            cancellationToken);
    }

    public Task SaveSessionAsync(
        string userId,
        string courseId,
        TrainerSessionSnapshot snapshot,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ArgumentException.ThrowIfNullOrWhiteSpace(courseId);
        ArgumentNullException.ThrowIfNull(snapshot);
        return _database.ExecuteAsync(
            connection =>
            {
                using var transaction = connection.BeginTransaction();
                using (var session = connection.CreateCommand())
                {
                    session.Transaction = transaction;
                    session.CommandText = """
                        INSERT INTO move_trainer_sessions(
                            id, user_id, course_id, mode, status, current_index,
                            started_utc, completed_utc, updated_utc)
                        VALUES($id, $user, $course, 'due', $status, $index,
                               $started, $completed, $updated)
                        ON CONFLICT(id) DO UPDATE SET
                            status = excluded.status,
                            current_index = excluded.current_index,
                            completed_utc = excluded.completed_utc,
                            updated_utc = excluded.updated_utc;
                        """;
                    session.Parameters.AddWithValue("$id", snapshot.SessionId);
                    session.Parameters.AddWithValue("$user", userId);
                    session.Parameters.AddWithValue("$course", courseId);
                    session.Parameters.AddWithValue("$status", snapshot.IsComplete ? "completed" : "active");
                    session.Parameters.AddWithValue("$index", snapshot.CurrentIndex);
                    session.Parameters.AddWithValue("$started", now.ToString("O"));
                    session.Parameters.AddWithValue("$completed", snapshot.IsComplete ? now.ToString("O") : DBNull.Value);
                    session.Parameters.AddWithValue("$updated", now.ToString("O"));
                    session.ExecuteNonQuery();
                }

                using (var clear = connection.CreateCommand())
                {
                    clear.Transaction = transaction;
                    clear.CommandText = "DELETE FROM move_trainer_session_items WHERE session_id = $session;";
                    clear.Parameters.AddWithValue("$session", snapshot.SessionId);
                    clear.ExecuteNonQuery();
                }

                for (var index = 0; index < snapshot.Items.Count; index++)
                {
                    var state = snapshot.Items[index];
                    using var insert = connection.CreateCommand();
                    insert.Transaction = transaction;
                    insert.CommandText = """
                        INSERT INTO move_trainer_session_items(
                            session_id, ordinal, item_id, status, attempt_count,
                            last_outcome, had_mistake)
                        VALUES($session, $ordinal, $item, $status, $attempts,
                               $outcome, $mistake);
                        """;
                    insert.Parameters.AddWithValue("$session", snapshot.SessionId);
                    insert.Parameters.AddWithValue("$ordinal", index);
                    insert.Parameters.AddWithValue("$item", state.ItemId);
                    insert.Parameters.AddWithValue("$status", state.Completed ? "completed" : "pending");
                    insert.Parameters.AddWithValue("$attempts", state.AttemptCount);
                    insert.Parameters.AddWithValue("$outcome", state.Outcome is null ? DBNull.Value : DbName(state.Outcome.Value));
                    insert.Parameters.AddWithValue(
                        "$mistake",
                        snapshot.MistakeItemIds.Contains(state.ItemId, StringComparer.Ordinal) ? 1 : 0);
                    insert.ExecuteNonQuery();
                }

                transaction.Commit();
                return 0;
            },
            cancellationToken);
    }

    public Task<TrainerSessionSnapshot?> GetLatestActiveSessionAsync(
        string userId,
        string courseId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ArgumentException.ThrowIfNullOrWhiteSpace(courseId);
        return _database.ExecuteAsync(
            connection =>
            {
                string? sessionId = null;
                var currentIndex = 0;
                using (var session = connection.CreateCommand())
                {
                    session.CommandText = """
                        SELECT id, current_index
                        FROM move_trainer_sessions
                        WHERE user_id = $user AND course_id = $course AND status = 'active'
                        ORDER BY updated_utc DESC, id DESC
                        LIMIT 1;
                        """;
                    session.Parameters.AddWithValue("$user", userId);
                    session.Parameters.AddWithValue("$course", courseId);
                    using var reader = session.ExecuteReader();
                    if (reader.Read())
                    {
                        sessionId = reader.GetString(0);
                        currentIndex = reader.GetInt32(1);
                    }
                }

                if (sessionId is null)
                {
                    return null;
                }

                var states = new List<TrainerSessionItemState>();
                var mistakes = new List<string>();
                using (var items = connection.CreateCommand())
                {
                    items.CommandText = """
                        SELECT item_id, attempt_count, last_outcome, status, had_mistake
                        FROM move_trainer_session_items
                        WHERE session_id = $session
                        ORDER BY ordinal;
                        """;
                    items.Parameters.AddWithValue("$session", sessionId);
                    using var reader = items.ExecuteReader();
                    while (reader.Read())
                    {
                        var itemId = reader.GetString(0);
                        var outcome = reader.IsDBNull(2) ? null : ParseOutcome(reader.GetString(2));
                        states.Add(new TrainerSessionItemState(
                            itemId,
                            reader.GetInt32(1),
                            outcome,
                            string.Equals(reader.GetString(3), "completed", StringComparison.Ordinal)));
                        if (reader.GetInt32(4) != 0)
                        {
                            mistakes.Add(itemId);
                        }
                    }
                }

                return states.Count == 0
                    ? null
                    : new TrainerSessionSnapshot(
                        sessionId,
                        Math.Clamp(currentIndex, 0, states.Count),
                        states,
                        mistakes,
                        currentIndex >= states.Count);
            },
            cancellationToken);
    }

    private RecordedPracticeAttempt RecordAttempt(
        SqliteConnection connection,
        string userId,
        TrainerCourse course,
        TrainerItem item,
        TrainerAttemptRequest request,
        TrainerEvaluation evaluation,
        DateTimeOffset reviewedAt,
        string sourceKind,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var transaction = connection.BeginTransaction();
        var cardKey = StableId.Create("practice-card", item.CourseId, item.NodeId, item.PositionKey);
        var cardId = StableId.Create("card", userId, cardKey);
        FsrsCard? before = null;
        var wasNew = true;
        using (var query = connection.CreateCommand())
        {
            query.Transaction = transaction;
            query.CommandText = """
                SELECT id, fsrs_state, fsrs_step, stability, difficulty, retrievability,
                       due_utc, last_review_utc,
                       COALESCE((SELECT COUNT(*) FROM practice_reviews r WHERE r.card_id = practice_cards.id), 0),
                       COALESCE((SELECT COUNT(*) FROM practice_reviews r WHERE r.card_id = practice_cards.id AND r.outcome = 'wrong'), 0)
                FROM practice_cards
                WHERE user_id = $user AND card_key = $key;
                """;
            query.Parameters.AddWithValue("$user", userId);
            query.Parameters.AddWithValue("$key", cardKey);
            using var reader = query.ExecuteReader();
            if (reader.Read())
            {
                wasNew = false;
                cardId = reader.GetString(0);
                before = new FsrsCard(
                    ParseState(reader.GetString(1)),
                    reader.IsDBNull(2) ? null : reader.GetInt32(2),
                    reader.GetDouble(3),
                    reader.GetDouble(4),
                    reader.GetDouble(5),
                    DateTimeOffset.Parse(reader.GetString(6)),
                    reader.IsDBNull(7) ? null : DateTimeOffset.Parse(reader.GetString(7)),
                    reader.GetInt32(8),
                    reader.GetInt32(9));
            }
        }

        var fsrs = _scheduler.Review(
            before,
            evaluation.Outcome,
            request.RequestedRating,
            reviewedAt,
            request.HintsUsed,
            request.ResponseMilliseconds,
            course.Settings.ScheduleMode,
            course.Settings.CustomIntervalDays,
            course.Settings.CyclicalRepetitions);
        var mistakeDelta = evaluation.Outcome == TrainerOutcome.Wrong ? 1 : 0;
        var successDelta = evaluation.Outcome == TrainerOutcome.Correct ? 1 : 0;
        var softDelta = evaluation.Outcome == TrainerOutcome.SoftFail ? 1 : 0;
        using (var card = connection.CreateCommand())
        {
            card.Transaction = transaction;
            card.CommandText = """
                INSERT INTO practice_cards(
                    id, user_id, course_id, item_id, block_id, block_type, card_key,
                    prompt, fen, orientation, expected_json, source_json,
                    mistake_count, success_count, soft_fail_count, fsrs_state,
                    fsrs_step, stability, difficulty, retrievability, due_utc,
                    last_review_utc, last_source_kind, created_utc, updated_utc)
                VALUES($id, $user, $course, $item, $block, 'interactive-move', $key,
                       $prompt, $fen, $orientation, $expected, $source,
                       $mistakes, $successes, $soft, $state, $step, $stability,
                       $difficulty, $retrievability, $due, $reviewed, $sourceKind,
                       $created, $updated)
                ON CONFLICT(user_id, card_key) DO UPDATE SET
                    course_id = excluded.course_id,
                    item_id = excluded.item_id,
                    prompt = excluded.prompt,
                    fen = excluded.fen,
                    orientation = excluded.orientation,
                    expected_json = excluded.expected_json,
                    mistake_count = practice_cards.mistake_count + excluded.mistake_count,
                    success_count = practice_cards.success_count + excluded.success_count,
                    soft_fail_count = practice_cards.soft_fail_count + excluded.soft_fail_count,
                    fsrs_state = excluded.fsrs_state,
                    fsrs_step = excluded.fsrs_step,
                    stability = excluded.stability,
                    difficulty = excluded.difficulty,
                    retrievability = excluded.retrievability,
                    due_utc = excluded.due_utc,
                    last_review_utc = excluded.last_review_utc,
                    last_source_kind = excluded.last_source_kind,
                    updated_utc = excluded.updated_utc;
                """;
            card.Parameters.AddWithValue("$id", cardId);
            card.Parameters.AddWithValue("$user", userId);
            card.Parameters.AddWithValue("$course", course.Id);
            card.Parameters.AddWithValue("$item", item.Id);
            card.Parameters.AddWithValue("$block", item.NodeId);
            card.Parameters.AddWithValue("$key", cardKey);
            card.Parameters.AddWithValue("$prompt", item.Prompt);
            card.Parameters.AddWithValue("$fen", item.Fen);
            card.Parameters.AddWithValue(
                "$orientation",
                TrainerOrientation.FromFen(item.Fen) == ChessMentor.Chess.BoardOrientation.Black ? "black" : "white");
            card.Parameters.AddWithValue("$expected", JsonSerializer.Serialize(item.Answers, JsonOptions));
            card.Parameters.AddWithValue("$source", JsonSerializer.Serialize(new { item.GameId, item.NodeId }, JsonOptions));
            card.Parameters.AddWithValue("$mistakes", mistakeDelta);
            card.Parameters.AddWithValue("$successes", successDelta);
            card.Parameters.AddWithValue("$soft", softDelta);
            card.Parameters.AddWithValue("$state", DbName(fsrs.After.State));
            card.Parameters.AddWithValue("$step", fsrs.After.Step is null ? DBNull.Value : fsrs.After.Step.Value);
            card.Parameters.AddWithValue("$stability", fsrs.After.Stability);
            card.Parameters.AddWithValue("$difficulty", fsrs.After.Difficulty);
            card.Parameters.AddWithValue("$retrievability", fsrs.After.Retrievability);
            card.Parameters.AddWithValue("$due", fsrs.After.DueUtc.ToString("O"));
            card.Parameters.AddWithValue("$reviewed", reviewedAt.ToString("O"));
            card.Parameters.AddWithValue("$sourceKind", sourceKind);
            card.Parameters.AddWithValue("$created", reviewedAt.ToString("O"));
            card.Parameters.AddWithValue("$updated", reviewedAt.ToString("O"));
            card.ExecuteNonQuery();
        }

        var attemptId = $"attempt_{Guid.NewGuid():N}";
        using (var attempt = connection.CreateCommand())
        {
            attempt.Transaction = transaction;
            attempt.CommandText = """
                INSERT INTO practice_attempts(
                    id, user_id, course_id, item_id, block_id, block_type,
                    source_kind, attempt_kind, card_key, start_fen, result_fen,
                    move_uci, move_san, selected_piece, from_square, to_square,
                    input_method, hints_used, response_ms, outcome, is_correct,
                    score, grade, feedback, payload_json, created_utc)
                VALUES($id, $user, $course, $item, $block, 'interactive-move',
                       $source, $kind, $key, $start, $result, $uci, $san, $piece,
                       $from, $to, $input, $hints, $response, $outcome, $correct,
                       $score, $grade, $feedback, $payload, $created);
                """;
            attempt.Parameters.AddWithValue("$id", attemptId);
            attempt.Parameters.AddWithValue("$user", userId);
            attempt.Parameters.AddWithValue("$course", course.Id);
            attempt.Parameters.AddWithValue("$item", item.Id);
            attempt.Parameters.AddWithValue("$block", item.NodeId);
            attempt.Parameters.AddWithValue("$source", sourceKind);
            attempt.Parameters.AddWithValue("$kind", wasNew ? "new" : "review");
            attempt.Parameters.AddWithValue("$key", cardKey);
            attempt.Parameters.AddWithValue("$start", item.Fen);
            attempt.Parameters.AddWithValue("$result", evaluation.ResultFen);
            attempt.Parameters.AddWithValue("$uci", evaluation.MoveUci);
            attempt.Parameters.AddWithValue("$san", evaluation.MoveSan);
            attempt.Parameters.AddWithValue("$piece", request.SelectedPiece);
            attempt.Parameters.AddWithValue("$from", request.FromSquare);
            attempt.Parameters.AddWithValue("$to", request.ToSquare);
            attempt.Parameters.AddWithValue("$input", DbName(request.InputMethod));
            attempt.Parameters.AddWithValue("$hints", Math.Max(0, request.HintsUsed));
            attempt.Parameters.AddWithValue("$response", Math.Clamp(request.ResponseMilliseconds, 0, 600_000));
            attempt.Parameters.AddWithValue("$outcome", DbName(evaluation.Outcome));
            attempt.Parameters.AddWithValue("$correct", evaluation.Accepted ? 1 : 0);
            attempt.Parameters.AddWithValue("$score", Math.Clamp(evaluation.Score, 0, 100));
            attempt.Parameters.AddWithValue("$grade", evaluation.MatchedAnswer?.Kind.ToString() ?? string.Empty);
            attempt.Parameters.AddWithValue("$feedback", evaluation.Feedback);
            attempt.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(request, JsonOptions));
            attempt.Parameters.AddWithValue("$created", reviewedAt.ToString("O"));
            attempt.ExecuteNonQuery();
        }

        using (var context = connection.CreateCommand())
        {
            context.Transaction = transaction;
            context.CommandText = """
                INSERT INTO practice_attempt_contexts(
                    attempt_id, block_snapshot_json, input_method, hints_used,
                    client_data_json, created_utc)
                VALUES($attempt, $snapshot, $input, $hints, '{}', $created);
                """;
            context.Parameters.AddWithValue("$attempt", attemptId);
            context.Parameters.AddWithValue("$snapshot", JsonSerializer.Serialize(item, JsonOptions));
            context.Parameters.AddWithValue("$input", DbName(request.InputMethod));
            context.Parameters.AddWithValue("$hints", Math.Max(0, request.HintsUsed));
            context.Parameters.AddWithValue("$created", reviewedAt.ToString("O"));
            context.ExecuteNonQuery();
        }

        var reviewId = $"review_{Guid.NewGuid():N}";
        using (var review = connection.CreateCommand())
        {
            review.Transaction = transaction;
            review.CommandText = """
                INSERT INTO practice_reviews(
                    id, user_id, card_id, course_id, source_kind, move_uci,
                    move_san, outcome, requested_rating, applied_rating,
                    response_ms, fsrs_before_json, fsrs_after_json,
                    review_log_json, created_utc)
                VALUES($id, $user, $card, $course, $source, $uci, $san, $outcome,
                       $requested, $applied, $response, $before, $after, $log, $created);
                """;
            review.Parameters.AddWithValue("$id", reviewId);
            review.Parameters.AddWithValue("$user", userId);
            review.Parameters.AddWithValue("$card", cardId);
            review.Parameters.AddWithValue("$course", course.Id);
            review.Parameters.AddWithValue("$source", sourceKind);
            review.Parameters.AddWithValue("$uci", evaluation.MoveUci);
            review.Parameters.AddWithValue("$san", evaluation.MoveSan);
            review.Parameters.AddWithValue("$outcome", DbName(evaluation.Outcome));
            review.Parameters.AddWithValue("$requested", DbName(fsrs.RequestedRating));
            review.Parameters.AddWithValue("$applied", DbName(fsrs.AppliedRating));
            review.Parameters.AddWithValue("$response", fsrs.ReviewDurationMilliseconds);
            review.Parameters.AddWithValue("$before", JsonSerializer.Serialize(fsrs.Before, JsonOptions));
            review.Parameters.AddWithValue("$after", JsonSerializer.Serialize(fsrs.After, JsonOptions));
            review.Parameters.AddWithValue("$log", JsonSerializer.Serialize(fsrs, JsonOptions));
            review.Parameters.AddWithValue("$created", reviewedAt.ToString("O"));
            review.ExecuteNonQuery();
        }

        UpsertProfile(connection, transaction, userId, course.Id, sourceKind, reviewedAt);
        MirrorLegacyFsrs(connection, transaction, userId, item.Id, fsrs.After);
        transaction.Commit();
        return new RecordedPracticeAttempt(attemptId, cardId, evaluation, fsrs, wasNew);
    }

    private static void UpsertProfile(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string userId,
        string courseId,
        string sourceKind,
        DateTimeOffset now)
    {
        var course = sourceKind == "course_runtime";
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO move_trainer_profiles(
                user_id, course_id, first_course_data_utc, last_course_data_utc,
                first_trainer_data_utc, last_trainer_data_utc, course_attempts,
                trainer_attempts, total_attempts, last_source_kind,
                created_utc, updated_utc)
            VALUES($user, $course, $firstCourse, $lastCourse, $firstTrainer,
                   $lastTrainer, $courseCount, $trainerCount, 1, $source, $now, $now)
            ON CONFLICT(user_id, course_id) DO UPDATE SET
                first_course_data_utc = COALESCE(move_trainer_profiles.first_course_data_utc, excluded.first_course_data_utc),
                last_course_data_utc = COALESCE(excluded.last_course_data_utc, move_trainer_profiles.last_course_data_utc),
                first_trainer_data_utc = COALESCE(move_trainer_profiles.first_trainer_data_utc, excluded.first_trainer_data_utc),
                last_trainer_data_utc = COALESCE(excluded.last_trainer_data_utc, move_trainer_profiles.last_trainer_data_utc),
                course_attempts = move_trainer_profiles.course_attempts + excluded.course_attempts,
                trainer_attempts = move_trainer_profiles.trainer_attempts + excluded.trainer_attempts,
                total_attempts = move_trainer_profiles.total_attempts + 1,
                last_source_kind = excluded.last_source_kind,
                updated_utc = excluded.updated_utc;
            """;
        command.Parameters.AddWithValue("$user", userId);
        command.Parameters.AddWithValue("$course", courseId);
        command.Parameters.AddWithValue("$firstCourse", course ? now.ToString("O") : DBNull.Value);
        command.Parameters.AddWithValue("$lastCourse", course ? now.ToString("O") : DBNull.Value);
        command.Parameters.AddWithValue("$firstTrainer", course ? DBNull.Value : now.ToString("O"));
        command.Parameters.AddWithValue("$lastTrainer", course ? DBNull.Value : now.ToString("O"));
        command.Parameters.AddWithValue("$courseCount", course ? 1 : 0);
        command.Parameters.AddWithValue("$trainerCount", course ? 0 : 1);
        command.Parameters.AddWithValue("$source", sourceKind);
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        command.ExecuteNonQuery();
    }

    private static void MirrorLegacyFsrs(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string userId,
        string itemId,
        FsrsCard card)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO fsrs_state(
                user_id, item_id, due_utc, stability, difficulty, elapsed_days,
                scheduled_days, repetitions, lapses, last_review_utc, state_json)
            VALUES($user, $item, $due, $stability, $difficulty, 0, $scheduled,
                   $repetitions, $lapses, $last, $json)
            ON CONFLICT(user_id, item_id) DO UPDATE SET
                due_utc = excluded.due_utc,
                stability = excluded.stability,
                difficulty = excluded.difficulty,
                elapsed_days = excluded.elapsed_days,
                scheduled_days = excluded.scheduled_days,
                repetitions = excluded.repetitions,
                lapses = excluded.lapses,
                last_review_utc = excluded.last_review_utc,
                state_json = excluded.state_json;
            """;
        command.Parameters.AddWithValue("$user", userId);
        command.Parameters.AddWithValue("$item", itemId);
        command.Parameters.AddWithValue("$due", card.DueUtc.ToString("O"));
        command.Parameters.AddWithValue("$stability", card.Stability);
        command.Parameters.AddWithValue("$difficulty", card.Difficulty);
        command.Parameters.AddWithValue("$scheduled", Math.Max(0, (int)Math.Ceiling((card.DueUtc - (card.LastReviewUtc ?? card.DueUtc)).TotalDays)));
        command.Parameters.AddWithValue("$repetitions", card.Repetitions);
        command.Parameters.AddWithValue("$lapses", card.Lapses);
        command.Parameters.AddWithValue("$last", card.LastReviewUtc?.ToString("O") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(card, JsonOptions));
        command.ExecuteNonQuery();
    }

    private static QueueData ReadQueueData(
        SqliteConnection connection,
        string userId,
        TrainerCourse course,
        DateTimeOffset now)
    {
        var cards = new Dictionary<string, CardQueueState>(StringComparer.Ordinal);
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT item_id, due_utc, mistake_count, success_count, difficulty, updated_utc
                FROM practice_cards
                WHERE user_id = $user AND course_id = $course AND item_id IS NOT NULL;
                """;
            command.Parameters.AddWithValue("$user", userId);
            command.Parameters.AddWithValue("$course", course.Id);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                cards[reader.GetString(0)] = new CardQueueState(
                    DateTimeOffset.Parse(reader.GetString(1)),
                    reader.GetInt32(2),
                    reader.GetInt32(3),
                    reader.GetDouble(4),
                    DateTimeOffset.Parse(reader.GetString(5)));
            }
        }

        var candidates = course.Items.Where(static item => item.Enabled).Select(item =>
        {
            if (!cards.TryGetValue(item.Id, out var card))
            {
                return new TrainerQueueCandidate(item, true, now, 0, 0, 5, course.UpdatedUtc);
            }

            return new TrainerQueueCandidate(
                item,
                false,
                card.DueUtc,
                card.MistakeCount,
                card.SuccessCount,
                card.Difficulty,
                card.UpdatedUtc);
        }).ToArray();
        var dayStart = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero).ToString("O");
        using var count = connection.CreateCommand();
        count.CommandText = """
            SELECT
                COALESCE(SUM(CASE WHEN attempt_kind = 'new' THEN 1 ELSE 0 END), 0),
                COALESCE(SUM(CASE WHEN attempt_kind = 'review' THEN 1 ELSE 0 END), 0)
            FROM practice_attempts
            WHERE user_id = $user AND course_id = $course AND created_utc >= $start;
            """;
        count.Parameters.AddWithValue("$user", userId);
        count.Parameters.AddWithValue("$course", course.Id);
        count.Parameters.AddWithValue("$start", dayStart);
        using var counts = count.ExecuteReader();
        counts.Read();
        return new QueueData(candidates, counts.GetInt32(0), counts.GetInt32(1));
    }

    private static MoveTrainerStats ReadStats(
        SqliteConnection connection,
        string userId,
        string? courseId,
        DateTimeOffset now)
    {
        var courseFilter = string.IsNullOrWhiteSpace(courseId) ? string.Empty : " AND course_id = $course";
        using var attempts = connection.CreateCommand();
        attempts.CommandText = $"""
            SELECT COUNT(*),
                   COALESCE(SUM(CASE WHEN outcome = 'correct' THEN 1 ELSE 0 END), 0),
                   COALESCE(SUM(CASE WHEN outcome = 'soft_fail' THEN 1 ELSE 0 END), 0),
                   COALESCE(SUM(CASE WHEN outcome = 'wrong' THEN 1 ELSE 0 END), 0)
            FROM practice_attempts
            WHERE user_id = $user{courseFilter};
            """;
        attempts.Parameters.AddWithValue("$user", userId);
        if (!string.IsNullOrWhiteSpace(courseId))
        {
            attempts.Parameters.AddWithValue("$course", courseId);
        }

        using var attemptReader = attempts.ExecuteReader();
        attemptReader.Read();
        var total = attemptReader.GetInt32(0);
        var correct = attemptReader.GetInt32(1);
        var soft = attemptReader.GetInt32(2);
        var wrong = attemptReader.GetInt32(3);

        using var cards = connection.CreateCommand();
        cards.CommandText = $"""
            SELECT COUNT(*),
                   COALESCE(SUM(CASE WHEN due_utc <= $now THEN 1 ELSE 0 END), 0),
                   COALESCE(AVG(difficulty), 0),
                   COALESCE(AVG(retrievability), 0)
            FROM practice_cards
            WHERE user_id = $user{courseFilter};
            """;
        cards.Parameters.AddWithValue("$user", userId);
        cards.Parameters.AddWithValue("$now", now.ToString("O"));
        if (!string.IsNullOrWhiteSpace(courseId))
        {
            cards.Parameters.AddWithValue("$course", courseId);
        }

        using var cardReader = cards.ExecuteReader();
        cardReader.Read();
        return new MoveTrainerStats(
            total,
            correct,
            soft,
            wrong,
            cardReader.GetInt32(0),
            cardReader.GetInt32(1),
            total == 0 ? 0 : Math.Round(correct * 100d / total, 1),
            total == 0 ? 0 : Math.Round((correct + soft) * 100d / total, 1),
            Math.Round(cardReader.GetDouble(2), 2),
            Math.Round(cardReader.GetDouble(3) * 100, 1));
    }

    private static TrainerCourse? LoadCourse(SqliteConnection connection, string courseId)
    {
        string title;
        string settingsJson;
        string sourcePgn;
        DateTimeOffset updated;
        using (var course = connection.CreateCommand())
        {
            course.CommandText = """
                SELECT title, settings_json, source_pgn, updated_utc
                FROM move_trainer_courses WHERE id = $id;
                """;
            course.Parameters.AddWithValue("$id", courseId);
            using var reader = course.ExecuteReader();
            if (!reader.Read())
            {
                return null;
            }

            title = reader.GetString(0);
            settingsJson = reader.GetString(1);
            sourcePgn = reader.GetString(2);
            updated = DateTimeOffset.Parse(reader.GetString(3));
        }

        TrainerCourseSettings settings;
        try
        {
            settings = JsonSerializer.Deserialize<TrainerCourseSettings>(settingsJson, JsonOptions)
                ?? new TrainerCourseSettings();
        }
        catch (JsonException)
        {
            settings = new TrainerCourseSettings();
        }

        var items = new List<TrainerItem>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT id, game_id, node_id, fen, answer_json, transposition_key
                FROM move_trainer_items
                WHERE course_id = $course
                ORDER BY rowid;
                """;
            command.Parameters.AddWithValue("$course", courseId);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var payload = ReadPayload(reader.GetString(4));
                items.Add(new TrainerItem(
                    reader.GetString(0),
                    courseId,
                    reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
                    reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                    reader.GetString(3),
                    reader.IsDBNull(5) ? payload.PositionKey : reader.GetString(5),
                    payload.Answers,
                    payload.Hints,
                    payload.Prompt,
                    payload.WrongMoveFeedback,
                    payload.Priority,
                    payload.Enabled));
            }
        }

        return new TrainerCourse(courseId, title, items, settings.Normalize(), sourcePgn, updated);
    }

    private static PersistedTrainerItem ReadPayload(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                var payload = JsonSerializer.Deserialize<PersistedTrainerItem>(json, JsonOptions);
                if (payload is not null)
                {
                    return payload;
                }
            }
            else if (document.RootElement.ValueKind == JsonValueKind.Array)
            {
                var legacyAnswers = JsonSerializer.Deserialize<TrainerAnswer[]>(json, JsonOptions);
                if (legacyAnswers is not null)
                {
                    return new PersistedTrainerItem(
                        legacyAnswers,
                        Array.Empty<TrainerHint>(),
                        "حرکت صحیح را پیدا کنید.",
                        "این حرکت پاسخ تمرین نیست.",
                        50,
                        true,
                        string.Empty);
                }
            }
        }
        catch (JsonException)
        {
        }

        return new PersistedTrainerItem(
            Array.Empty<TrainerAnswer>(),
            Array.Empty<TrainerHint>(),
            "حرکت صحیح را پیدا کنید.",
            "این حرکت پاسخ تمرین نیست.",
            50,
            true,
            string.Empty);
    }

    private static FsrsLearningState ParseState(string value) =>
        value switch
        {
            "learning" => FsrsLearningState.Learning,
            "review" => FsrsLearningState.Review,
            "relearning" => FsrsLearningState.Relearning,
            _ => FsrsLearningState.New,
        };

    private static TrainerOutcome? ParseOutcome(string value) => value switch
    {
        "correct" => TrainerOutcome.Correct,
        "soft_fail" => TrainerOutcome.SoftFail,
        "wrong" => TrainerOutcome.Wrong,
        _ => null,
    };

    private static string DbName(TrainerOutcome outcome) => outcome switch
    {
        TrainerOutcome.Correct => "correct",
        TrainerOutcome.SoftFail => "soft_fail",
        _ => "wrong",
    };

    private static string DbName(FsrsLearningState state) => state switch
    {
        FsrsLearningState.Learning => "learning",
        FsrsLearningState.Review => "review",
        FsrsLearningState.Relearning => "relearning",
        _ => "new",
    };

    private static string DbName(TrainerRating rating) => rating.ToString().ToLowerInvariant();
    private static string DbName(TrainerInputMethod input) => input.ToString().ToLowerInvariant();

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }

    private sealed record PersistedTrainerItem(
        IReadOnlyList<TrainerAnswer> Answers,
        IReadOnlyList<TrainerHint> Hints,
        string Prompt,
        string WrongMoveFeedback,
        int Priority,
        bool Enabled,
        string PositionKey);

    private sealed record CardQueueState(
        DateTimeOffset DueUtc,
        int MistakeCount,
        int SuccessCount,
        double Difficulty,
        DateTimeOffset UpdatedUtc);

    private sealed record QueueData(
        IReadOnlyList<TrainerQueueCandidate> Candidates,
        int NewToday,
        int ReviewsToday);
}
