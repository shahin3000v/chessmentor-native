using System.Text.Json;
using System.Text.Json.Serialization;
using ChessMentor.Core;

namespace ChessMentor.CourseBuilder;

public enum CourseBlockKind
{
    Text,
    Position,
    InteractiveMove,
    MoveSequence,
    Variation,
    Hint,
    Audio,
    Stage,
    Checkpoint,
}

public enum CourseSourceKind
{
    Game,
    Move,
    Comment,
    Position,
    Variation,
}

public enum RevealMode { Off, Click }
public enum RevealTarget { Content, Annotations, All }
public enum WaitMode { Off, Click, Seconds }
public enum AudioFlow { Continue, Wait }
public enum AcceptedMoveGrade { Best, Good, Playable }

public sealed record CourseSourceReference(
    string SourceId,
    string GameId,
    string? NodeId,
    CourseSourceKind Kind);

public sealed record BoardAnnotation(
    string Kind,
    string From,
    string? To = null,
    string Tone = "accent");

public sealed record AcceptedMove(
    string Uci,
    AcceptedMoveGrade Grade = AcceptedMoveGrade.Best,
    int Score = 100,
    string Feedback = "");

public sealed record MultipleChoiceOption(
    string Id,
    string Kind,
    string Value,
    bool IsCorrect,
    int Score = 0,
    string Feedback = "");

public sealed record CourseAudioAttachment(
    string Id,
    string Source,
    bool Autoplay = false,
    bool PlayOnce = false,
    AudioFlow Flow = AudioFlow.Continue,
    string Scope = "teacher");

public sealed record RevealModifier(
    RevealMode Mode = RevealMode.Off,
    RevealTarget Target = RevealTarget.All);

public sealed record WaitModifier(
    WaitMode Mode = WaitMode.Off,
    double Seconds = 2,
    string Label = "ادامه");

public sealed record ScoreModifier(
    int FirstAttempt = 100,
    int SecondAttempt = 70,
    int LaterAttempt = 40,
    int HintPenalty = 10);

public sealed record CourseBlock(
    string Id,
    CourseBlockKind Kind,
    string Title = "",
    string Text = "",
    string? Fen = null,
    CourseSourceReference? Source = null,
    string? AttachedToBlockId = null,
    double? AutoAdvanceSeconds = null,
    RevealModifier? Reveal = null,
    WaitModifier? Wait = null,
    ScoreModifier? Score = null,
    IReadOnlyList<string>? StageMemberIds = null,
    IReadOnlyList<BoardAnnotation>? Annotations = null,
    IReadOnlyList<AcceptedMove>? AcceptedMoves = null,
    IReadOnlyList<string>? MoveSequence = null,
    IReadOnlyList<IReadOnlyList<string>>? AnswerBranches = null,
    IReadOnlyList<MultipleChoiceOption>? Options = null,
    IReadOnlyList<CourseAudioAttachment>? Audio = null,
    string? AdvancedKind = null)
{
    public CourseBlock Normalize() => this with
    {
        Title = (Title ?? string.Empty).Trim(),
        Text = Text ?? string.Empty,
        AutoAdvanceSeconds = AutoAdvanceSeconds is { } seconds
            ? Math.Clamp(seconds, 0.2, 120)
            : null,
        Wait = Wait is { } wait
            ? wait with { Seconds = Math.Clamp(wait.Seconds, 0.2, 120) }
            : new WaitModifier(),
        Reveal = Reveal ?? new RevealModifier(),
        Score = Score ?? new ScoreModifier(),
        StageMemberIds = (StageMemberIds ?? Array.Empty<string>())
            .Where(static id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToArray(),
        Annotations = (Annotations ?? Array.Empty<BoardAnnotation>()).Take(128).ToArray(),
        AcceptedMoves = (AcceptedMoves ?? Array.Empty<AcceptedMove>()).Take(24).ToArray(),
        MoveSequence = (MoveSequence ?? Array.Empty<string>()).Take(256).ToArray(),
        AnswerBranches = (AnswerBranches ?? Array.Empty<IReadOnlyList<string>>())
            .Take(32)
            .Select(static branch => (IReadOnlyList<string>)branch.Take(8).ToArray())
            .ToArray(),
        Options = (Options ?? Array.Empty<MultipleChoiceOption>()).Take(20).ToArray(),
        Audio = (Audio ?? Array.Empty<CourseAudioAttachment>()).Take(8).ToArray(),
    };
}

public sealed record CourseBuilderDocument(
    string Id,
    string Title,
    IReadOnlyList<CourseBlock> Blocks,
    string SourcePgn = "",
    string? ServerCourseId = null,
    int SchemaVersion = 1,
    DateTimeOffset UpdatedUtc = default)
{
    public static CourseBuilderDocument Create(string title = "دوره جدید")
    {
        var now = DateTimeOffset.UtcNow;
        return new CourseBuilderDocument(
            StableId.Create("builder-course", Guid.NewGuid()),
            title,
            Array.Empty<CourseBlock>(),
            UpdatedUtc: now);
    }

    public CourseBuilderDocument Normalize()
    {
        var normalized = (Blocks ?? Array.Empty<CourseBlock>())
            .Select(static block => block.Normalize())
            .ToArray();
        if (normalized.Select(static block => block.Id).Distinct(StringComparer.Ordinal).Count() != normalized.Length)
        {
            throw new InvalidDataException("Course Builder document contains duplicate block IDs.");
        }

        var ids = normalized.Select(static block => block.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var block in normalized)
        {
            if (block.AttachedToBlockId is { } target && (!ids.Contains(target) || target == block.Id))
            {
                throw new InvalidDataException($"Block '{block.Id}' has an invalid LEGO attachment target.");
            }

            if (block.Kind != CourseBlockKind.Text && block.AttachedToBlockId is not null)
            {
                throw new InvalidDataException("Only Text blocks can be attached as LEGO content.");
            }

            if (block.StageMemberIds!.Any(member => !ids.Contains(member) || member == block.Id))
            {
                throw new InvalidDataException($"Stage '{block.Id}' references an invalid member.");
            }
        }

        return this with
        {
            Title = string.IsNullOrWhiteSpace(Title) ? "دوره بدون عنوان" : Title.Trim(),
            Blocks = normalized,
            UpdatedUtc = UpdatedUtc == default ? DateTimeOffset.UtcNow : UpdatedUtc,
        };
    }
}

public static class CourseBuilderJson
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    public static string Serialize(CourseBuilderDocument document) =>
        JsonSerializer.Serialize(document.Normalize(), Options);

    public static CourseBuilderDocument Deserialize(string json) =>
        (JsonSerializer.Deserialize<CourseBuilderDocument>(json, Options) ??
         throw new InvalidDataException("Course Builder JSON is empty or invalid.")).Normalize();

    public static CourseBuilderDocument Clone(CourseBuilderDocument document) =>
        Deserialize(Serialize(document));

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}
