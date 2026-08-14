using ChessMentor.Chess;
using ChessMentor.MoveTrainer;
using ChessMentor.Pgn;

namespace ChessMentor.Tests;

public sealed class MoveTrainerDomainTests
{
    [Fact]
    public void AnswerPolicyAcceptsPrimarySoftFailAndConfiguredTransposition()
    {
        var token = TestContext.Current.CancellationToken;
        var rules = ManagedChessRules.Instance;
        var d4Fen = rules.ApplyMove(FenPosition.Initial, "d2d4", token);
        var item = Item([
            new TrainerAnswer("e2e4", "e4", TrainerAnswerKind.Primary),
            new TrainerAnswer(
                "c2c4",
                "c4",
                TrainerAnswerKind.Alternate,
                ResultPositionKey: ManagedChessRules.PositionKey(d4Fen)),
            new TrainerAnswer("g1f3", "Nf3", TrainerAnswerKind.SoftFail, "قابل قبول"),
        ]);
        var evaluator = new TrainerAnswerEvaluator(rules);

        var primary = evaluator.Evaluate(
            item,
            new TrainerAttemptRequest("e2e4", TrainerInputMethod.Drag),
            acceptTranspositions: true,
            cancellationToken: token);
        Assert.Equal(TrainerOutcome.Correct, primary.Outcome);
        Assert.False(primary.IsTransposition);

        var soft = evaluator.Evaluate(
            item,
            new TrainerAttemptRequest("g1f3", TrainerInputMethod.Click),
            acceptTranspositions: true,
            cancellationToken: token);
        Assert.Equal(TrainerOutcome.SoftFail, soft.Outcome);
        Assert.Equal(70, soft.Score);

        var transposition = evaluator.Evaluate(
            item,
            new TrainerAttemptRequest("d2d4", TrainerInputMethod.Drag),
            acceptTranspositions: true,
            cancellationToken: token);
        Assert.Equal(TrainerOutcome.Correct, transposition.Outcome);
        Assert.True(transposition.IsTransposition);

        var rejectedWithoutPolicy = evaluator.Evaluate(
            item,
            new TrainerAttemptRequest("d2d4", TrainerInputMethod.Drag),
            acceptTranspositions: false,
            cancellationToken: token);
        Assert.Equal(TrainerOutcome.Wrong, rejectedWithoutPolicy.Outcome);
    }

    [Fact]
    public void WrongPieceAndMistakesStayPendingThenRetryAsFreshAttempt()
    {
        var token = TestContext.Current.CancellationToken;
        var first = Item([new TrainerAnswer("e2e4", "e4", TrainerAnswerKind.Primary)]);
        var second = Item(
            [new TrainerAnswer("d2d4", "d4", TrainerAnswerKind.Primary)],
            id: "item-2");
        var session = new MoveTrainerSession(
            [first, second],
            new TrainerCourseSettings(AllowRetry: true));

        var wrongPiece = session.Submit(new TrainerAttemptRequest(
            "a7a6",
            TrainerInputMethod.Drag,
            SelectedPiece: "p",
            FromSquare: "a7",
            ToSquare: "a6"), token);
        Assert.Equal(TrainerOutcome.Wrong, wrongPiece.Outcome);
        Assert.False(wrongPiece.IsLegal);
        Assert.Equal(first.Id, session.CurrentItem?.Id);

        Assert.Equal(
            TrainerOutcome.Correct,
            session.Submit(new TrainerAttemptRequest("e2e4", TrainerInputMethod.Click), token).Outcome);
        Assert.Equal(second.Id, session.CurrentItem?.Id);
        var restored = MoveTrainerSession.Restore(
            [first, second],
            new TrainerCourseSettings(AllowRetry: true),
            session.Snapshot());
        Assert.Equal(second.Id, restored.CurrentItem?.Id);
        Assert.Contains(first.Id, restored.Snapshot().MistakeItemIds);
        Assert.Equal(
            TrainerOutcome.Correct,
            session.Submit(new TrainerAttemptRequest("d2d4", TrainerInputMethod.Click), token).Outcome);
        Assert.True(session.IsComplete);
        Assert.Contains(first.Id, session.Snapshot().MistakeItemIds);

        Assert.True(session.RetryMistakes());
        Assert.Equal(1, session.Total);
        Assert.Equal(first.Id, session.CurrentItem?.Id);
        Assert.Equal(0, Assert.Single(session.Snapshot().Items).AttemptCount);
    }

    [Fact]
    public void FsrsIsDeterministicAndMapsWrongSoftFailAndHints()
    {
        var scheduler = new FsrsScheduler();
        var now = DateTimeOffset.FromUnixTimeSeconds(1_900_000_000);
        var first = scheduler.Review(
            null,
            TrainerOutcome.Correct,
            TrainerRating.Good,
            now,
            scheduleMode: TrainerScheduleMode.Custom,
            customIntervalDays: 3);
        var repeated = scheduler.Review(
            null,
            TrainerOutcome.Correct,
            TrainerRating.Good,
            now,
            scheduleMode: TrainerScheduleMode.Custom,
            customIntervalDays: 3);
        Assert.Equal(first, repeated);
        Assert.Equal(TrainerRating.Good, first.AppliedRating);
        Assert.Equal(now.AddDays(3), first.After.DueUtc);

        var assisted = scheduler.Review(
            first.After,
            TrainerOutcome.Correct,
            TrainerRating.Easy,
            now.AddMinutes(1),
            hintsUsed: 1);
        Assert.Equal(TrainerRating.Hard, assisted.AppliedRating);

        var soft = scheduler.Review(
            assisted.After,
            TrainerOutcome.SoftFail,
            TrainerRating.Easy,
            now.AddMinutes(2));
        Assert.Equal(TrainerRating.Hard, soft.AppliedRating);

        var wrong = scheduler.Review(
            soft.After,
            TrainerOutcome.Wrong,
            TrainerRating.Easy,
            now.AddMinutes(3));
        Assert.Equal(TrainerRating.Again, wrong.AppliedRating);
        Assert.Equal(FsrsLearningState.Relearning, wrong.After.State);
        Assert.Equal(1, wrong.After.Lapses);
        Assert.Equal(1, FsrsScheduler.Retrievability(10, 0));
        Assert.Equal(0.9, FsrsScheduler.Retrievability(10, 10));
    }

    [Fact]
    public void QueueHonorsDueFirstDailyLimitsAndStableOrdering()
    {
        var now = DateTimeOffset.Parse("2030-01-02T12:00:00Z");
        var candidates = new[]
        {
            Candidate("new-high", isNew: true, now, priority: 90),
            Candidate("new-low", isNew: true, now, priority: 10),
            Candidate("due-late", isNew: false, now.AddMinutes(-1), mistakes: 2),
            Candidate("due-early", isNew: false, now.AddDays(-1), mistakes: 1),
            Candidate("future", isNew: false, now.AddDays(1), mistakes: 9),
        };
        var plan = new TrainerQueuePlanner().Build(
            candidates,
            new TrainerCourseSettings(DailyNewLimit: 2, DailyReviewLimit: 2, MaxSessionItems: 3),
            now,
            newCompletedToday: 1,
            reviewsCompletedToday: 0);

        Assert.Equal(["due-early", "due-late", "new-high"], plan.Items.Select(static item => item.Item.Id));
        Assert.Equal(1, plan.NewCount);
        Assert.Equal(2, plan.ReviewCount);
        Assert.Equal(0, plan.RemainingNewToday);
    }

    [Fact]
    public void QueueFiltersPositionsByConfiguredSideToMove()
    {
        var token = TestContext.Current.CancellationToken;
        var now = DateTimeOffset.Parse("2030-01-02T12:00:00Z");
        var white = Candidate("white", isNew: true, now);
        var black = Candidate("black", isNew: true, now) with
        {
            Item = Item([new TrainerAnswer("e7e5", "e5", TrainerAnswerKind.Primary)], "black") with
            {
                Fen = ManagedChessRules.Instance.ApplyMove(FenPosition.Initial, "e2e4", token),
            },
        };
        var planner = new TrainerQueuePlanner();

        var whitePlan = planner.Build(
            [white, black],
            new TrainerCourseSettings(Side: TrainerSide.White),
            now,
            0,
            0);
        var blackPlan = planner.Build(
            [white, black],
            new TrainerCourseSettings(Side: TrainerSide.Black),
            now,
            0,
            0);

        Assert.Equal("white", Assert.Single(whitePlan.Items).Item.Id);
        Assert.Equal("black", Assert.Single(blackPlan.Items).Item.Id);
    }

    [Fact]
    public async Task PgnFactoryKeepsMultipleGamesVariationsAndStableSourceReferences()
    {
        var parser = new PgnParser();
        var document = await parser.ParseAsync(
            "[Event \"A\"]\n\n1. e4 (1. d4) e5 *\n\n[Event \"B\"]\n\n1. c4 e5 *",
            TestContext.Current.CancellationToken);
        await new PgnSemanticEnricher().EnrichAsync(document, TestContext.Current.CancellationToken);

        var course = new MoveTrainerCourseFactory().CreateCandidateCourse(
            "Multi",
            [document],
            document.Serialize(),
            courseId: "course-1");

        Assert.Equal(2, course.Items.Select(static item => item.GameId).Distinct().Count());
        var root = course.Items.Single(item => item.GameId == document.Games[0].Id &&
                                               item.NodeId == document.Games[0].Root.StableId);
        Assert.Equal(2, root.Answers.Count);
        Assert.Equal(TrainerAnswerKind.Primary, root.Answers[0].Kind);
        Assert.Equal(TrainerAnswerKind.Alternate, root.Answers[1].Kind);
    }

    private static TrainerItem Item(IReadOnlyList<TrainerAnswer> answers, string id = "item-1") =>
        new(
            id,
            "course-1",
            "game-1",
            "node-1",
            FenPosition.Initial,
            ManagedChessRules.PositionKey(FenPosition.Initial),
            answers,
            Array.Empty<TrainerHint>(),
            "حرکت صحیح را پیدا کنید.",
            "اشتباه بود.");

    private static TrainerQueueCandidate Candidate(
        string id,
        bool isNew,
        DateTimeOffset due,
        int priority = 50,
        int mistakes = 0) =>
        new(
            Item([new TrainerAnswer("e2e4", "e4", TrainerAnswerKind.Primary)], id) with
            {
                Priority = priority,
            },
            isNew,
            due,
            mistakes,
            0,
            5,
            due);
}
