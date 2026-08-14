namespace ChessMentor.MoveTrainer;

public sealed class TrainerQueuePlanner
{
    public TrainerQueuePlan Build(
        IEnumerable<TrainerQueueCandidate> candidates,
        TrainerCourseSettings settings,
        DateTimeOffset now,
        int newCompletedToday,
        int reviewsCompletedToday)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(settings);
        var normalized = settings.Normalize();
        var materialized = candidates
            .Where(candidate => MatchesSide(candidate.Item.Fen, normalized.Side))
            .ToArray();
        var remainingNew = Math.Max(0, normalized.DailyNewLimit - Math.Max(0, newCompletedToday));
        var remainingReviews = Math.Max(0, normalized.DailyReviewLimit - Math.Max(0, reviewsCompletedToday));
        var reviews = materialized
            .Where(candidate => !candidate.IsNew && candidate.DueUtc <= now)
            .OrderBy(static candidate => candidate.DueUtc)
            .ThenByDescending(static candidate => candidate.MistakeCount - candidate.SuccessCount)
            .ThenByDescending(static candidate => candidate.Difficulty)
            .ThenByDescending(static candidate => candidate.Item.Priority)
            .ThenBy(static candidate => candidate.Item.Id, StringComparer.Ordinal)
            .Take(remainingReviews)
            .ToArray();
        var fresh = materialized
            .Where(static candidate => candidate.IsNew)
            .OrderByDescending(static candidate => candidate.Item.Priority)
            .ThenBy(static candidate => candidate.UpdatedUtc)
            .ThenBy(static candidate => candidate.Item.Id, StringComparer.Ordinal)
            .Take(remainingNew)
            .ToArray();
        var selected = reviews.Concat(fresh)
            .Take(normalized.MaxSessionItems)
            .ToArray();
        return new TrainerQueuePlan(
            selected,
            selected.Count(static candidate => candidate.IsNew),
            selected.Count(static candidate => !candidate.IsNew),
            Math.Max(0, remainingNew - selected.Count(static candidate => candidate.IsNew)),
            Math.Max(0, remainingReviews - selected.Count(static candidate => !candidate.IsNew)));
    }

    private static bool MatchesSide(string fen, TrainerSide side)
    {
        if (side == TrainerSide.Both)
        {
            return true;
        }

        var fields = fen.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var blackToMove = fields.Length > 1 && string.Equals(fields[1], "b", StringComparison.Ordinal);
        return side == TrainerSide.Black ? blackToMove : !blackToMove;
    }
}
