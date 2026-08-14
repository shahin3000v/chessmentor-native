using ChessMentor.Core.Mvvm;
using ChessMentor.Pgn;

namespace ChessMentor.Viewer;

public enum ViewerMoveDisplayMode
{
    All,
    Training,
    Mobile,
}

public enum ViewerNotationMode
{
    Letters,
    Figurines,
}

public sealed class ViewerGameItem : ObservableObject
{
    private readonly IReadOnlyDictionary<string, ViewerMoveItem> _moveIndex;
    private readonly IReadOnlyDictionary<string, ViewerMoveRow> _moveRowIndex;
    private int _index;
    private bool _isMarked;
    private bool _isActive;

    internal ViewerGameItem(PgnGame game, string sourceFileName, int index)
    {
        Game = game;
        SourceFileName = sourceFileName;
        _index = index;
        MoveItems = ViewerMoveListBuilder.Build(game);
        MoveRows = ViewerMoveRowBuilder.Build(MoveItems);
        _moveIndex = MoveItems.ToDictionary(static move => move.NodeId, StringComparer.Ordinal);
        _moveRowIndex = MoveRows
            .SelectMany(static row => row.Moves.Select(move => (move.NodeId, Row: row)))
            .ToDictionary(static pair => pair.NodeId, static pair => pair.Row, StringComparer.Ordinal);
    }

    public PgnGame Game { get; }
    public string SourceFileName { get; }
    public IReadOnlyList<ViewerMoveItem> MoveItems { get; }
    public IReadOnlyList<ViewerMoveRow> MoveRows { get; }
    public int Index => _index;
    public string White => Game.Header("White") ?? "سفید";
    public string Black => Game.Header("Black") ?? "سیاه";
    public string Result => Game.Header("Result") ?? Game.Result;
    public string Title => $"{Index + 1}. {White} – {Black}";
    public string FullTitle => $"{Title}  {Result}";
    public string RootComment => ViewerText.JoinComments(Game.Root.Comments);
    public IReadOnlyList<PgnHeader> Headers => Game.Headers;

    public bool IsMarked
    {
        get => _isMarked;
        set => SetProperty(ref _isMarked, value);
    }

    public bool IsActive
    {
        get => _isActive;
        internal set => SetProperty(ref _isActive, value);
    }

    internal void Reindex(int index)
    {
        if (_index == index)
        {
            return;
        }

        _index = index;
        OnPropertyChanged(nameof(Index));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(FullTitle));
    }

    internal ViewerMoveItem? FindMove(string nodeId) =>
        _moveIndex.TryGetValue(nodeId, out var move) ? move : null;

    public ViewerMoveRow? FindMoveRow(string nodeId) =>
        _moveRowIndex.TryGetValue(nodeId, out var row) ? row : null;
}

public sealed class ViewerMoveItem : ObservableObject
{
    private bool _isActive;
    private ViewerNotationMode _notationMode;

    internal ViewerMoveItem(PgnMoveNode node, int depth, bool startsVariation, bool showBlackNumber)
    {
        Node = node;
        Depth = depth;
        StartsVariation = startsVariation;
        IsWhiteMove = node.IsWhiteMove ?? node.Ply % 2 == 1;
        FullmoveNumber = node.FullmoveNumber ?? Math.Max(1, (node.Ply + 1) / 2);
        MoveNumber = IsWhiteMove
            ? $"{FullmoveNumber}."
            : showBlackNumber ? $"{FullmoveNumber}..." : string.Empty;
    }

    public PgnMoveNode Node { get; }
    public string NodeId => Node.StableId;
    public int Depth { get; }
    public double Indent => Math.Min(72, Depth * 18d);
    public bool StartsVariation { get; }
    public bool IsWhiteMove { get; }
    public int FullmoveNumber { get; }
    public string MoveNumber { get; }
    public string San => Node.RawSan;
    public string DisplaySan => ViewerNotation.FormatSan(Node.RawSan, IsWhiteMove, _notationMode);
    public string Uci => Node.Uci ?? string.Empty;
    public string NagText => Node.Nags.Count == 0
        ? string.Empty
        : string.Join(" ", Node.Nags.Select(static nag => $"${nag}"));
    public string StartingComment => ViewerText.JoinComments(Node.StartingComments);
    public string Comment => ViewerText.JoinComments(Node.Comments);
    public bool HasStartingComment => !string.IsNullOrWhiteSpace(StartingComment);
    public bool HasComment => !string.IsNullOrWhiteSpace(Comment);

    public bool IsActive
    {
        get => _isActive;
        internal set => SetProperty(ref _isActive, value);
    }

    public void SetNotationMode(ViewerNotationMode notationMode)
    {
        if (_notationMode == notationMode)
        {
            return;
        }

        _notationMode = notationMode;
        OnPropertyChanged(nameof(DisplaySan));
    }
}

/// <summary>
/// A standard chess-score row. White and black keep fixed visual columns while
/// comments may expand vertically. A comment after White forces Black onto the
/// next visual line without changing its logical full-move row.
/// </summary>
public sealed class ViewerMoveRow
{
    internal ViewerMoveRow(ViewerMoveItem? whiteMove, ViewerMoveItem? blackMove)
    {
        WhiteMove = whiteMove;
        BlackMove = blackMove;
        var first = whiteMove ?? blackMove
            ?? throw new ArgumentException("A move row must contain at least one move.");
        FullmoveNumber = first.FullmoveNumber;
        Depth = first.Depth;
        StartsVariation = first.StartsVariation;
    }

    public ViewerMoveItem? WhiteMove { get; }
    public ViewerMoveItem? BlackMove { get; }
    public IEnumerable<ViewerMoveItem> Moves
    {
        get
        {
            if (WhiteMove is not null)
            {
                yield return WhiteMove;
            }

            if (BlackMove is not null)
            {
                yield return BlackMove;
            }
        }
    }

    public int FullmoveNumber { get; }
    public int Depth { get; }
    public double Indent => Math.Min(54, Depth * 14d);
    public bool StartsVariation { get; }
    public string MoveNumber => WhiteMove is null ? $"{FullmoveNumber}..." : $"{FullmoveNumber}.";
    public bool BlackOnNextLine =>
        BlackMove is not null &&
        (WhiteMove?.HasComment == true || BlackMove.HasStartingComment);
}

public sealed record ViewerBranchItem(PgnMoveNode Node, int Index)
{
    public string NodeId => Node.StableId;
    public bool IsWhiteMove => Node.IsWhiteMove ?? Node.Ply % 2 == 1;
    public int FullmoveNumber => Node.FullmoveNumber ?? Math.Max(1, (Node.Ply + 1) / 2);
    public string Label => $"{FullmoveNumber}{(IsWhiteMove ? "." : "...")} {Node.RawSan}";
    public bool IsMainline => Index == 0;
}

public enum ViewerNavigationResult
{
    None,
    Moved,
    BranchSelectionRequired,
}

internal static class ViewerMoveListBuilder
{
    public static IReadOnlyList<ViewerMoveItem> Build(PgnGame game)
    {
        var items = new List<ViewerMoveItem>(Math.Max(4, game.NodeCount));
        if (game.Root.Children.Count > 0)
        {
            AppendLine(game.Root.Children[0], 0, false, items);
        }

        return items;
    }

    private static void AppendLine(
        PgnMoveNode start,
        int depth,
        bool forceFirstBlackNumber,
        List<ViewerMoveItem> items)
    {
        PgnMoveNode? node = start;
        var first = true;
        var numberBlack = forceFirstBlackNumber;
        while (node is not null)
        {
            var tokenForcesNumber = node.ForceMoveNumber ||
                node.MoveNumberToken?.RawText.Contains("...", StringComparison.Ordinal) == true;
            var isWhiteMove = node.IsWhiteMove ?? node.Ply % 2 == 1;
            items.Add(new ViewerMoveItem(
                node,
                depth,
                startsVariation: first && depth > 0,
                showBlackNumber: !isWhiteMove && (numberBlack || tokenForcesNumber || (first && depth > 0))));

            var variationJustRendered = false;
            var parent = node.Parent;
            if (parent is not null && parent.Children.Count > 1 && ReferenceEquals(parent.Children[0], node))
            {
                for (var index = 1; index < parent.Children.Count; index++)
                {
                    AppendLine(parent.Children[index], depth + 1, true, items);
                }

                variationJustRendered = true;
            }

            node = node.Children.FirstOrDefault();
            numberBlack = variationJustRendered && node is not null && !(node.IsWhiteMove ?? node.Ply % 2 == 1);
            first = false;
        }
    }
}

internal static class ViewerMoveRowBuilder
{
    public static IReadOnlyList<ViewerMoveRow> Build(IReadOnlyList<ViewerMoveItem> moves)
    {
        var rows = new List<ViewerMoveRow>(Math.Max(2, (moves.Count + 1) / 2));
        for (var index = 0; index < moves.Count; index++)
        {
            var current = moves[index];
            if (!current.IsWhiteMove)
            {
                rows.Add(new ViewerMoveRow(null, current));
                continue;
            }

            ViewerMoveItem? black = null;
            if (index + 1 < moves.Count)
            {
                var candidate = moves[index + 1];
                if (!candidate.IsWhiteMove &&
                    candidate.FullmoveNumber == current.FullmoveNumber &&
                    candidate.Depth == current.Depth &&
                    !candidate.StartsVariation)
                {
                    black = candidate;
                    index++;
                }
            }

            rows.Add(new ViewerMoveRow(current, black));
        }

        return rows;
    }
}

public static class ViewerText
{
    private static readonly IReadOnlyDictionary<char, char> LatinDigitMap =
        new Dictionary<char, char>
        {
            ['۰'] = '0', ['۱'] = '1', ['۲'] = '2', ['۳'] = '3', ['۴'] = '4',
            ['۵'] = '5', ['۶'] = '6', ['۷'] = '7', ['۸'] = '8', ['۹'] = '9',
            ['٠'] = '0', ['١'] = '1', ['٢'] = '2', ['٣'] = '3', ['٤'] = '4',
            ['٥'] = '5', ['٦'] = '6', ['٧'] = '7', ['٨'] = '8', ['٩'] = '9',
        };

    public static string NormalizeCommentForDisplay(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return string.Create(
            value.Length,
            value,
            static (target, source) =>
            {
                for (var index = 0; index < source.Length; index++)
                {
                    target[index] = LatinDigitMap.TryGetValue(source[index], out var digit)
                        ? digit
                        : source[index];
                }
            });
    }

    internal static string JoinComments(IEnumerable<PgnComment> comments) =>
        NormalizeCommentForDisplay(string.Join(Environment.NewLine, comments.Select(static comment => comment.Text)));
}

public static class ViewerNotation
{
    private static readonly IReadOnlyDictionary<char, char> WhiteFigurines =
        new Dictionary<char, char> { ['K'] = '♔', ['Q'] = '♕', ['R'] = '♖', ['B'] = '♗', ['N'] = '♘' };
    private static readonly IReadOnlyDictionary<char, char> BlackFigurines =
        new Dictionary<char, char> { ['K'] = '♚', ['Q'] = '♛', ['R'] = '♜', ['B'] = '♝', ['N'] = '♞' };

    public static string FormatSan(string san, bool isWhiteMove, ViewerNotationMode mode)
    {
        if (mode == ViewerNotationMode.Letters || string.IsNullOrEmpty(san))
        {
            return san;
        }

        var map = isWhiteMove ? WhiteFigurines : BlackFigurines;
        var characters = san.ToCharArray();
        if (characters.Length > 0 && map.TryGetValue(characters[0], out var leading))
        {
            characters[0] = leading;
        }

        for (var index = 1; index < characters.Length; index++)
        {
            if (characters[index - 1] == '=' && map.TryGetValue(characters[index], out var promotion))
            {
                characters[index] = promotion;
            }
        }

        return new string(characters);
    }
}
