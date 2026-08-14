using ChessMentor.Chess;

namespace ChessMentor.MoveTrainer;

public enum TrainerAnswerKind
{
    Primary,
    Alternate,
    SoftFail,
}

public enum TrainerOutcome
{
    Correct,
    SoftFail,
    Wrong,
}

public enum TrainerRating
{
    Again,
    Hard,
    Good,
    Easy,
}

public enum TrainerInputMethod
{
    Click,
    Drag,
    Keyboard,
}

public enum TrainerSide
{
    Both,
    White,
    Black,
}

public enum TrainerScheduleMode
{
    Spaced,
    Custom,
    Cyclical,
}

public sealed record TrainerAnswer(
    string Uci,
    string San,
    TrainerAnswerKind Kind,
    string Feedback = "",
    string? ResultPositionKey = null);

public sealed record TrainerHint(
    string Kind,
    string Text,
    int Penalty = 0,
    string? FromSquare = null,
    string? ToSquare = null);

public sealed record TrainerItem(
    string Id,
    string CourseId,
    string GameId,
    string NodeId,
    string Fen,
    string PositionKey,
    IReadOnlyList<TrainerAnswer> Answers,
    IReadOnlyList<TrainerHint> Hints,
    string Prompt,
    string WrongMoveFeedback,
    int Priority = 50,
    bool Enabled = true);

public sealed record TrainerCourseSettings(
    TrainerSide Side = TrainerSide.Both,
    TrainerScheduleMode ScheduleMode = TrainerScheduleMode.Spaced,
    bool AcceptTranspositions = true,
    bool AllowRetry = true,
    int DailyNewLimit = 20,
    int DailyReviewLimit = 100,
    int MaxSessionItems = 50,
    int CustomIntervalDays = 1,
    int CyclicalRepetitions = 1)
{
    public TrainerCourseSettings Normalize() => this with
    {
        DailyNewLimit = Math.Clamp(DailyNewLimit, 0, 500),
        DailyReviewLimit = Math.Clamp(DailyReviewLimit, 0, 2000),
        MaxSessionItems = Math.Clamp(MaxSessionItems, 1, 500),
        CustomIntervalDays = Math.Clamp(CustomIntervalDays, 1, 3650),
        CyclicalRepetitions = Math.Clamp(CyclicalRepetitions, 1, 3650),
    };
}

public sealed record TrainerCourse(
    string Id,
    string Title,
    IReadOnlyList<TrainerItem> Items,
    TrainerCourseSettings Settings,
    string SourcePgn,
    DateTimeOffset UpdatedUtc);

public sealed record TrainerAttemptRequest(
    string MoveUci,
    TrainerInputMethod InputMethod,
    string SelectedPiece = "",
    string FromSquare = "",
    string ToSquare = "",
    int HintsUsed = 0,
    int ResponseMilliseconds = 0,
    TrainerRating RequestedRating = TrainerRating.Good);

public sealed record TrainerEvaluation(
    TrainerOutcome Outcome,
    bool Accepted,
    bool StrictlyCorrect,
    bool IsLegal,
    bool IsTransposition,
    string MoveUci,
    string MoveSan,
    string ResultFen,
    string ResultPositionKey,
    TrainerAnswer? MatchedAnswer,
    string Feedback,
    int Score)
{
    public bool CompletesItem => Outcome is TrainerOutcome.Correct or TrainerOutcome.SoftFail;
}

public sealed record TrainerQueueCandidate(
    TrainerItem Item,
    bool IsNew,
    DateTimeOffset DueUtc,
    int MistakeCount,
    int SuccessCount,
    double Difficulty,
    DateTimeOffset UpdatedUtc);

public sealed record TrainerQueuePlan(
    IReadOnlyList<TrainerQueueCandidate> Items,
    int NewCount,
    int ReviewCount,
    int RemainingNewToday,
    int RemainingReviewsToday);

public sealed record TrainerSessionItemState(
    string ItemId,
    int AttemptCount,
    TrainerOutcome? Outcome,
    bool Completed);

public sealed record TrainerSessionSnapshot(
    string SessionId,
    int CurrentIndex,
    IReadOnlyList<TrainerSessionItemState> Items,
    IReadOnlyList<string> MistakeItemIds,
    bool IsComplete);

public static class TrainerOrientation
{
    public static BoardOrientation FromFen(string fen)
    {
        var fields = (fen ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return fields.Length > 1 && string.Equals(fields[1], "b", StringComparison.Ordinal)
            ? BoardOrientation.Black
            : BoardOrientation.White;
    }
}
