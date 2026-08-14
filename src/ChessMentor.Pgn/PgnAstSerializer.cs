using System.Text;

namespace ChessMentor.Pgn;

/// <summary>
/// Deterministic serializer for an authored AST. Imported documents still use
/// <see cref="PgnSerializer"/> for byte-for-byte token preservation.
/// </summary>
public static class PgnAstSerializer
{
    private static readonly IReadOnlyDictionary<string, int> AnnotationNags =
        new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["!"] = 1,
            ["?"] = 2,
            ["!!"] = 3,
            ["??"] = 4,
            ["!?"] = 5,
            ["?!"] = 6,
        };

    public static string SerializeGames(IEnumerable<PgnGame> games)
    {
        ArgumentNullException.ThrowIfNull(games);
        var rendered = games.Select(SerializeGame).ToArray();
        return rendered.Length == 0
            ? string.Empty
            : string.Join(Environment.NewLine + Environment.NewLine, rendered) + Environment.NewLine;
    }

    public static string SerializeGame(PgnGame game)
    {
        ArgumentNullException.ThrowIfNull(game);
        var output = new StringBuilder();
        var hasResultHeader = false;
        foreach (var header in game.Headers)
        {
            hasResultHeader |= string.Equals(header.Name, "Result", StringComparison.OrdinalIgnoreCase);
            output.Append('[')
                .Append(header.Name)
                .Append(" \"")
                .Append(EscapeHeader(header.Value))
                .AppendLine("\"]");
        }

        if (!hasResultHeader)
        {
            output.Append("[Result \"").Append(EscapeHeader(game.Result)).AppendLine("\"]");
        }

        output.AppendLine();
        var tokens = new List<string>();
        AppendComments(tokens, game.Root.StartingComments);
        AppendComments(tokens, game.Root.Comments);
        AppendPosition(tokens, game.Root, forceFirstNumber: true);
        tokens.Add(string.IsNullOrWhiteSpace(game.Result) ? "*" : game.Result);
        output.Append(string.Join(' ', tokens.Where(static token => token.Length > 0)));
        return output.ToString();
    }

    private static void AppendPosition(List<string> tokens, PgnMoveNode parent, bool forceFirstNumber)
    {
        if (parent.Children.Count == 0)
        {
            return;
        }

        var mainline = parent.Children[0];
        AppendMove(tokens, mainline, forceFirstNumber);
        for (var index = 1; index < parent.Children.Count; index++)
        {
            var variation = new List<string>();
            AppendBranch(variation, parent.Children[index]);
            tokens.Add("(" + string.Join(' ', variation) + ")");
        }

        AppendPosition(tokens, mainline, parent.Children.Count > 1);
    }

    private static void AppendBranch(List<string> tokens, PgnMoveNode firstMove)
    {
        AppendMove(tokens, firstMove, forceNumber: true);
        AppendPosition(tokens, firstMove, forceFirstNumber: false);
    }

    private static void AppendMove(List<string> tokens, PgnMoveNode node, bool forceNumber)
    {
        AppendComments(tokens, node.StartingComments);
        var white = node.IsWhiteMove ?? node.Ply % 2 == 1;
        var number = node.FullmoveNumber ?? Math.Max(1, (node.Ply + 1) / 2);
        if (white)
        {
            tokens.Add($"{number}.");
        }
        else if (forceNumber || node.ForceMoveNumber ||
                 node.MoveNumberToken?.RawText.Contains("...", StringComparison.Ordinal) == true)
        {
            tokens.Add($"{number}...");
        }

        tokens.Add(node.RawSan);
        var annotationNags = new HashSet<int>();
        foreach (var annotation in node.Annotations)
        {
            tokens.Add(annotation);
            if (AnnotationNags.TryGetValue(annotation, out var nag))
            {
                annotationNags.Add(nag);
            }
        }

        foreach (var nag in node.Nags.Distinct().Where(nag => !annotationNags.Contains(nag)).Order())
        {
            tokens.Add($"${nag}");
        }

        AppendComments(tokens, node.Comments);
    }

    private static void AppendComments(List<string> tokens, IEnumerable<PgnComment> comments)
    {
        foreach (var comment in comments)
        {
            var text = comment.Text.Replace('{', '(').Replace('}', ')').Replace("\0", " ", StringComparison.Ordinal);
            if (text.Length > 0)
            {
                tokens.Add("{" + text + "}");
            }
        }
    }

    private static string EscapeHeader(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace('\r', ' ')
            .Replace('\n', ' ');
}
