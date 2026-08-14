using ChessMentor.Chess;
using ChessMentor.Core;
using ChessMentor.Pgn;

namespace ChessMentor.MoveTrainer;

public sealed class MoveTrainerCourseFactory
{
    public TrainerCourse CreateCandidateCourse(
        string title,
        IEnumerable<PgnDocument> documents,
        string sourcePgn,
        TrainerCourseSettings? settings = null,
        string? courseId = null)
    {
        ArgumentNullException.ThrowIfNull(documents);
        var id = string.IsNullOrWhiteSpace(courseId)
            ? StableId.Create("trainer-course", title, sourcePgn)
            : courseId;
        var items = new List<TrainerItem>();
        foreach (var game in documents.SelectMany(static document => document.Games))
        {
            var pending = new Stack<PgnMoveNode>();
            pending.Push(game.Root);
            while (pending.TryPop(out var parent))
            {
                for (var index = parent.Children.Count - 1; index >= 0; index--)
                {
                    pending.Push(parent.Children[index]);
                }

                var fen = parent.Fen;
                var resolved = parent.Children
                    .Where(static child => !string.IsNullOrWhiteSpace(child.Uci) &&
                                             !string.IsNullOrWhiteSpace(child.PositionKey))
                    .ToArray();
                if (string.IsNullOrWhiteSpace(fen) || resolved.Length == 0)
                {
                    continue;
                }

                var answers = resolved.Select((child, index) => new TrainerAnswer(
                    child.Uci!,
                    child.RawSan,
                    index == 0 ? TrainerAnswerKind.Primary : TrainerAnswerKind.Alternate,
                    PgnTreeEditor.CommentText(child),
                    child.PositionKey)).ToArray();
                var comment = PgnTreeEditor.CommentText(parent);
                IReadOnlyList<TrainerHint> hints = string.IsNullOrWhiteSpace(comment)
                    ? Array.Empty<TrainerHint>()
                    : [new TrainerHint("text", comment, 10)];
                items.Add(new TrainerItem(
                    StableId.Create("trainer-item", id, game.Id, parent.StableId),
                    id,
                    game.Id,
                    parent.StableId,
                    fen,
                    parent.PositionKey ?? ManagedChessRules.PositionKey(fen),
                    answers,
                    hints,
                    "حرکت صحیح را پیدا کنید.",
                    "این حرکت با پاسخ‌های این تمرین منطبق نیست.",
                    Priority: 50));
            }
        }

        return new TrainerCourse(
            id,
            string.IsNullOrWhiteSpace(title) ? "دوره تمرینی بدون عنوان" : title.Trim(),
            items,
            (settings ?? new TrainerCourseSettings()).Normalize(),
            sourcePgn,
            DateTimeOffset.UtcNow);
    }
}
