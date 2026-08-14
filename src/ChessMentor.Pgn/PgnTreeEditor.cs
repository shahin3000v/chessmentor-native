using ChessMentor.Chess;
using ChessMentor.Core;

namespace ChessMentor.Pgn;

/// <summary>
/// Intentional AST mutations used by Studio. The token-preserving parser remains
/// the source of truth for imported text; authored structures are serialized by
/// <see cref="PgnAstSerializer"/> and reparsed before they replace a workspace.
/// </summary>
public static class PgnTreeEditor
{
    public static string StartingCommentText(PgnMoveNode node) =>
        JoinComments(node.StartingComments);

    public static string CommentText(PgnMoveNode node) =>
        JoinComments(node.Comments);

    public static void SetStartingComment(PgnMoveNode node, string? text)
    {
        ArgumentNullException.ThrowIfNull(node);
        ReplaceComments(node.StartingComments, node.ClearStartingComments, node.AddStartingComment, text);
    }

    public static void SetComment(PgnMoveNode node, string? text)
    {
        ArgumentNullException.ThrowIfNull(node);
        ReplaceComments(node.Comments, node.ClearComments, node.AddComment, text);
    }

    public static PgnMoveInsertResult AddMove(
        PgnGame game,
        PgnMoveNode parent,
        LegalMove move,
        string resultingFen)
    {
        ArgumentNullException.ThrowIfNull(game);
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(move);
        ArgumentException.ThrowIfNullOrWhiteSpace(resultingFen);

        var existing = parent.Children.FirstOrDefault(child =>
            string.Equals(child.Uci, move.Uci, StringComparison.Ordinal));
        if (existing is not null)
        {
            return new PgnMoveInsertResult(existing, false);
        }

        var occurrence = parent.Children.Count(child =>
            string.Equals(child.RawSan, move.San, StringComparison.Ordinal));
        var stableId = StableId.Create(
            "node",
            game.Id,
            parent.StableId,
            NormalizeSan(move.San),
            occurrence);
        var parentFen = parent.Fen ?? game.Root.Fen ?? FenPosition.Initial;
        var fenFields = parentFen.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var isWhiteMove = fenFields.Length < 2 || fenFields[1] == "w";
        var fullmoveNumber = fenFields.Length > 5 && int.TryParse(fenFields[5], out var fullmove)
            ? fullmove
            : Math.Max(1, (parent.Ply + 2) / 2);
        var node = new PgnMoveNode(parent, stableId, move.San, parent.Ply + 1, null)
        {
            Uci = move.Uci,
            Fen = resultingFen,
            PositionKey = ManagedChessRules.PositionKey(resultingFen),
            IsWhiteMove = isWhiteMove,
            FullmoveNumber = fullmoveNumber,
        };
        node.TranspositionGroupId = StableId.Create("position", node.PositionKey);
        parent.AddChild(node);
        return new PgnMoveInsertResult(node, true);
    }

    public static bool DeleteBranch(PgnMoveNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        return node.Parent?.RemoveChild(node) == true;
    }

    public static bool PromoteToMainline(PgnMoveNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        var parent = node.Parent;
        if (parent is null)
        {
            return false;
        }

        var index = parent.IndexOfChild(node);
        if (index <= 0)
        {
            return false;
        }

        parent.RemoveChild(node);
        parent.InsertChild(0, node);
        return true;
    }

    public static PgnExternalGameIdentity CaptureIdentity(PgnGame game)
    {
        ArgumentNullException.ThrowIfNull(game);
        return new PgnExternalGameIdentity(game.Id, CaptureNode(game.Root));
    }

    public static void ApplyIdentity(PgnGame game, PgnExternalGameIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(game);
        ArgumentNullException.ThrowIfNull(identity);
        game.Id = identity.GameId;
        ApplyNodeIdentity(game.Root, identity.Root);
    }

    /// <summary>
    /// Captures stable IDs without creating a recursively nested JSON graph.
    /// A long mainline can contain thousands of plies; serializing the legacy
    /// identity tree hits System.Text.Json's maximum depth during autosave.
    /// </summary>
    public static PgnFlatGameIdentity CaptureFlatIdentity(PgnGame game)
    {
        ArgumentNullException.ThrowIfNull(game);
        var nodes = new List<PgnFlatNodeIdentity>(game.NodeCount + 1);
        var pending = new Stack<PgnMoveNode>();
        pending.Push(game.Root);
        while (pending.TryPop(out var node))
        {
            nodes.Add(new PgnFlatNodeIdentity(nodes.Count, node.StableId, node.Children.Count));
            for (var index = node.Children.Count - 1; index >= 0; index--)
            {
                pending.Push(node.Children[index]);
            }
        }

        return new PgnFlatGameIdentity(game.Id, nodes);
    }

    public static void ApplyFlatIdentity(PgnGame game, PgnFlatGameIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(game);
        ArgumentNullException.ThrowIfNull(identity);
        var expected = CaptureFlatNodes(game);
        if (expected.Count != identity.Nodes.Count)
        {
            throw new InvalidDataException("The flat identity count does not match the PGN tree.");
        }

        for (var index = 0; index < expected.Count; index++)
        {
            var node = expected[index];
            var external = identity.Nodes[index];
            if (external.Ordinal != index ||
                external.ChildCount != node.Children.Count ||
                string.IsNullOrWhiteSpace(external.NodeId))
            {
                throw new InvalidDataException("The flat identity order does not match the PGN tree.");
            }

            node.StableId = external.NodeId;
        }

        game.Id = identity.GameId;
    }

    private static string JoinComments(IEnumerable<PgnComment> comments) =>
        string.Join(Environment.NewLine, comments.Select(static comment => comment.Text));

    private static void ReplaceComments(
        IReadOnlyList<PgnComment> existing,
        Action clear,
        Action<PgnComment> add,
        string? text)
    {
        foreach (var comment in existing)
        {
            comment.Token.ReplaceRawText(string.Empty);
        }

        clear();
        var clean = (text ?? string.Empty).Replace("\0", " ", StringComparison.Ordinal);
        if (string.IsNullOrWhiteSpace(clean))
        {
            return;
        }

        add(new PgnComment(new PgnToken(PgnTokenKind.BraceComment, "{" + clean + "}", -1, 0, 0)));
    }

    private static string NormalizeSan(string san) =>
        san.Replace("0-0-0", "O-O-O", StringComparison.Ordinal)
            .Replace("0-0", "O-O", StringComparison.Ordinal)
            .Trim();

    private static PgnExternalNodeIdentity CaptureNode(PgnMoveNode node) =>
        new(node.StableId, node.Children.Select(CaptureNode).ToArray());

    private static void ApplyNodeIdentity(PgnMoveNode node, PgnExternalNodeIdentity identity)
    {
        if (node.Children.Count != identity.Children.Count)
        {
            throw new InvalidDataException("The external node identity tree does not match the PGN tree.");
        }

        node.StableId = identity.NodeId;
        for (var index = 0; index < node.Children.Count; index++)
        {
            ApplyNodeIdentity(node.Children[index], identity.Children[index]);
        }
    }

    private static List<PgnMoveNode> CaptureFlatNodes(PgnGame game)
    {
        var result = new List<PgnMoveNode>(game.NodeCount + 1);
        var pending = new Stack<PgnMoveNode>();
        pending.Push(game.Root);
        while (pending.TryPop(out var node))
        {
            result.Add(node);
            for (var index = node.Children.Count - 1; index >= 0; index--)
            {
                pending.Push(node.Children[index]);
            }
        }

        return result;
    }
}

public sealed record PgnMoveInsertResult(PgnMoveNode Node, bool Created);
public sealed record PgnExternalGameIdentity(string GameId, PgnExternalNodeIdentity Root);
public sealed record PgnExternalNodeIdentity(string NodeId, IReadOnlyList<PgnExternalNodeIdentity> Children);
public sealed record PgnFlatGameIdentity(string GameId, IReadOnlyList<PgnFlatNodeIdentity> Nodes);
public sealed record PgnFlatNodeIdentity(int Ordinal, string NodeId, int ChildCount);
