namespace ChessMentor.MoveTrainer;

public enum FsrsLearningState
{
    New,
    Learning,
    Review,
    Relearning,
}

public sealed record FsrsCard(
    FsrsLearningState State,
    int? Step,
    double Stability,
    double Difficulty,
    double Retrievability,
    DateTimeOffset DueUtc,
    DateTimeOffset? LastReviewUtc,
    int Repetitions,
    int Lapses)
{
    public static FsrsCard New(DateTimeOffset now) =>
        new(FsrsLearningState.New, 0, 0, 5, 0, now, null, 0, 0);
}

public sealed record FsrsReviewResult(
    FsrsCard Before,
    FsrsCard After,
    TrainerRating RequestedRating,
    TrainerRating AppliedRating,
    int IntervalDays,
    int ReviewDurationMilliseconds);

/// <summary>
/// Deterministic, no-fuzz FSRS scheduling policy. The state is deliberately a
/// pure function of the previous card, response, and supplied timestamp so the
/// same migration/review always produces the same result.
/// </summary>
public sealed class FsrsScheduler
{
    public const double DefaultDecay = -0.1542;
    public static readonly double DefaultFactor = Math.Pow(0.9, 1 / DefaultDecay) - 1;

    public FsrsReviewResult Review(
        FsrsCard? existing,
        TrainerOutcome outcome,
        TrainerRating requestedRating,
        DateTimeOffset reviewedAt,
        int hintsUsed = 0,
        int reviewDurationMilliseconds = 0,
        TrainerScheduleMode scheduleMode = TrainerScheduleMode.Spaced,
        int customIntervalDays = 1,
        int cyclicalRepetitions = 1)
    {
        var before = existing ?? FsrsCard.New(reviewedAt);
        var applied = AppliedRating(outcome, requestedRating, hintsUsed);
        var elapsedDays = before.LastReviewUtc is null
            ? 0
            : Math.Max(0, (reviewedAt - before.LastReviewUtc.Value).TotalDays);
        var recall = Retrievability(before.Stability, elapsedDays);
        var difficulty = Math.Clamp(
            before.Difficulty + (applied switch
            {
                TrainerRating.Again => 1.2,
                TrainerRating.Hard => 0.35,
                TrainerRating.Good => -0.15,
                _ => -0.55,
            }),
            1,
            10);
        var firstReview = before.Repetitions == 0;
        var stability = firstReview
            ? applied switch
            {
                TrainerRating.Again => 0.2,
                TrainerRating.Hard => 0.5,
                TrainerRating.Good => 3,
                _ => 7,
            }
            : applied switch
            {
                TrainerRating.Again => Math.Max(0.2, before.Stability * 0.45),
                TrainerRating.Hard => Math.Max(0.5, before.Stability * (1.15 + ((1 - recall) * 0.25))),
                TrainerRating.Good => Math.Max(1, before.Stability * (1.8 + ((1 - recall) * 0.7))),
                _ => Math.Max(2, before.Stability * (2.6 + (1 - recall))),
            };
        stability = Math.Round(stability, 6, MidpointRounding.AwayFromZero);
        difficulty = Math.Round(difficulty, 6, MidpointRounding.AwayFromZero);

        var due = applied switch
        {
            TrainerRating.Again => reviewedAt.AddMinutes(10),
            TrainerRating.Hard when firstReview => reviewedAt.AddHours(4),
            TrainerRating.Hard => reviewedAt.AddDays(Math.Max(1, Math.Ceiling(stability * 0.7))),
            TrainerRating.Good => reviewedAt.AddDays(Math.Max(1, Math.Ceiling(stability))),
            _ => reviewedAt.AddDays(Math.Max(1, Math.Ceiling(stability * 1.3))),
        };
        if (outcome != TrainerOutcome.Wrong && scheduleMode == TrainerScheduleMode.Custom)
        {
            due = reviewedAt.AddDays(Math.Clamp(customIntervalDays, 1, 3650));
        }
        else if (outcome != TrainerOutcome.Wrong && scheduleMode == TrainerScheduleMode.Cyclical)
        {
            due = reviewedAt.AddDays(Math.Clamp(cyclicalRepetitions, 1, 3650));
        }

        var state = applied switch
        {
            TrainerRating.Again when before.Repetitions > 0 => FsrsLearningState.Relearning,
            TrainerRating.Again or TrainerRating.Hard when firstReview => FsrsLearningState.Learning,
            _ => FsrsLearningState.Review,
        };
        var after = new FsrsCard(
            state,
            state is FsrsLearningState.Learning or FsrsLearningState.Relearning ? 0 : null,
            stability,
            difficulty,
            1,
            due,
            reviewedAt,
            before.Repetitions + 1,
            before.Lapses + (outcome == TrainerOutcome.Wrong ? 1 : 0));
        var intervalDays = (int)Math.Ceiling(Math.Max(0, (due - reviewedAt).TotalDays));
        return new FsrsReviewResult(
            before,
            after,
            requestedRating,
            applied,
            intervalDays,
            Math.Clamp(reviewDurationMilliseconds, 0, 600_000));
    }

    public static TrainerRating AppliedRating(
        TrainerOutcome outcome,
        TrainerRating requested,
        int hintsUsed) =>
        outcome switch
        {
            TrainerOutcome.Wrong => TrainerRating.Again,
            TrainerOutcome.SoftFail => TrainerRating.Hard,
            _ when hintsUsed > 0 => TrainerRating.Hard,
            _ => requested,
        };

    public static double Retrievability(double stability, double elapsedDays)
    {
        if (stability <= 0)
        {
            return 0;
        }

        var value = Math.Pow(1 + (DefaultFactor * Math.Max(0, elapsedDays) / stability), DefaultDecay);
        return Math.Round(Math.Clamp(value, 0, 1), 6, MidpointRounding.AwayFromZero);
    }
}
