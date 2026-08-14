using ChessMentor.Core;
using ChessMentor.Pgn;

namespace ChessMentor.CourseBuilder;

public sealed record CourseSourceItem(
    string Id,
    CourseSourceKind Kind,
    string Label,
    string SourceId,
    string GameId,
    string? NodeId,
    string Text,
    string? Fen)
{
    public CourseSourceReference Reference => new(SourceId, GameId, NodeId, Kind);
}

public static class CourseSourceCatalog
{
    public static IReadOnlyList<CourseSourceItem> FromDocument(PgnDocument document, string sourceId)
    {
        var results = new List<CourseSourceItem>();
        foreach (var game in document.Games)
        {
            var gameLabel = $"{game.Header("White") ?? "?"} – {game.Header("Black") ?? "?"}";
            results.Add(new CourseSourceItem(
                StableId.Create("builder-source", sourceId, game.Id, "game"),
                CourseSourceKind.Game,
                gameLabel,
                sourceId,
                game.Id,
                null,
                gameLabel,
                game.Root.Fen));

            foreach (var node in game.Root.Descendants())
            {
                var moveLabel = node.FullmoveNumber is { } number
                    ? $"{number}{(node.IsWhiteMove == false ? "..." : ".")} {node.RawSan}"
                    : node.RawSan;
                results.Add(new CourseSourceItem(
                    StableId.Create("builder-source", sourceId, game.Id, node.StableId, "move"),
                    CourseSourceKind.Move,
                    moveLabel,
                    sourceId,
                    game.Id,
                    node.StableId,
                    node.RawSan,
                    node.Parent?.Fen));
                if (!string.IsNullOrWhiteSpace(node.Fen))
                {
                    results.Add(new CourseSourceItem(
                        StableId.Create("builder-source", sourceId, game.Id, node.StableId, "position"),
                        CourseSourceKind.Position,
                        $"موقعیت بعد از {moveLabel}",
                        sourceId,
                        game.Id,
                        node.StableId,
                        moveLabel,
                        node.Fen));
                }

                var comments = node.Comments.Concat(node.StartingComments).ToArray();
                for (var index = 0; index < comments.Length; index++)
                {
                    var text = comments[index].Text.Trim();
                    if (text.Length == 0)
                    {
                        continue;
                    }

                    results.Add(new CourseSourceItem(
                        StableId.Create("builder-source", sourceId, game.Id, node.StableId, "comment", index),
                        CourseSourceKind.Comment,
                        text,
                        sourceId,
                        game.Id,
                        node.StableId,
                        text,
                        node.Fen));
                }

                if (node.Children.Count > 1)
                {
                    results.Add(new CourseSourceItem(
                        StableId.Create("builder-source", sourceId, game.Id, node.StableId, "variation"),
                        CourseSourceKind.Variation,
                        $"{node.Children.Count} شاخه از {moveLabel}",
                        sourceId,
                        game.Id,
                        node.StableId,
                        string.Join(" / ", node.Children.Select(static child => child.RawSan)),
                        node.Fen));
                }
            }
        }

        return results;
    }
}
