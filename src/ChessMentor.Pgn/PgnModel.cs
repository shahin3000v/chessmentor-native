using ChessMentor.Core;

namespace ChessMentor.Pgn;

public enum PgnCommentKind
{
    Brace,
    Line,
}

public sealed class PgnComment
{
    internal PgnComment(PgnToken token)
    {
        Token = token;
        Kind = token.Kind == PgnTokenKind.LineComment ? PgnCommentKind.Line : PgnCommentKind.Brace;
    }

    public PgnToken Token { get; }
    public PgnCommentKind Kind { get; }

    public string Text
    {
        get
        {
            if (Token.RawText.Length == 0)
            {
                return string.Empty;
            }

            if (Kind == PgnCommentKind.Line)
            {
                return Token.RawText[1..];
            }

            var end = Token.RawText.EndsWith('}') ? Token.RawText.Length - 1 : Token.RawText.Length;
            return Token.RawText[1..end];
        }
        set
        {
            var clean = (value ?? string.Empty).Replace("\0", " ", StringComparison.Ordinal);
            Token.ReplaceRawText(Kind == PgnCommentKind.Brace
                ? "{" + clean.Replace('{', '(').Replace('}', ')') + "}"
                : ";" + clean.Replace('\r', ' ').Replace('\n', ' '));
        }
    }
}

public sealed record PgnHeader(string Name, string Value, PgnToken Token);

public sealed class PgnMoveNode
{
    private readonly List<PgnMoveNode> _children = [];
    private readonly List<PgnComment> _startingComments = [];
    private readonly List<PgnComment> _comments = [];
    private readonly List<int> _nags = [];
    private readonly List<string> _annotations = [];

    internal PgnMoveNode(PgnMoveNode? parent, string stableId, string rawSan, int ply, PgnToken? sanToken)
    {
        Parent = parent;
        StableId = stableId;
        RawSan = rawSan;
        Ply = ply;
        SanToken = sanToken;
    }

    public PgnMoveNode? Parent { get; }
    public string StableId { get; internal set; }
    public string RawSan { get; }
    public string? Uci { get; internal set; }
    public string? Fen { get; internal set; }
    public string? PositionKey { get; internal set; }
    public string? TranspositionGroupId { get; internal set; }
    public bool? IsWhiteMove { get; internal set; }
    public int? FullmoveNumber { get; internal set; }
    public bool ForceMoveNumber { get; internal set; }
    public int Ply { get; }
    public bool IsRoot => Parent is null;
    public PgnToken? SanToken { get; }
    public PgnToken? MoveNumberToken { get; internal set; }
    public IReadOnlyList<PgnMoveNode> Children => _children;
    public IReadOnlyList<PgnComment> StartingComments => _startingComments;
    public IReadOnlyList<PgnComment> Comments => _comments;
    public IReadOnlyList<int> Nags => _nags;
    public IReadOnlyList<string> Annotations => _annotations;

    internal void AddChild(PgnMoveNode child) => _children.Add(child);
    internal void InsertChild(int index, PgnMoveNode child) => _children.Insert(index, child);
    internal bool RemoveChild(PgnMoveNode child) => _children.Remove(child);
    internal int IndexOfChild(PgnMoveNode child) => _children.IndexOf(child);
    internal void ClearStartingComments() => _startingComments.Clear();
    internal void ClearComments() => _comments.Clear();
    internal void AddStartingComment(PgnComment comment) => _startingComments.Add(comment);
    internal void AddComment(PgnComment comment) => _comments.Add(comment);
    internal void AddNag(int nag)
    {
        if (!_nags.Contains(nag))
        {
            _nags.Add(nag);
        }
    }

    internal void AddAnnotation(string annotation)
    {
        if (!_annotations.Contains(annotation, StringComparer.Ordinal))
        {
            _annotations.Add(annotation);
        }
    }

    public IEnumerable<PgnMoveNode> Descendants()
    {
        var stack = new Stack<PgnMoveNode>(_children.AsEnumerable().Reverse());
        while (stack.TryPop(out var node))
        {
            yield return node;
            for (var index = node._children.Count - 1; index >= 0; index--)
            {
                stack.Push(node._children[index]);
            }
        }
    }
}

public sealed class PgnGame
{
    private readonly List<PgnHeader> _headers = [];

    internal PgnGame(int index)
    {
        Index = index;
        Id = $"game_pending_{index}";
        Root = new PgnMoveNode(null, $"root_pending_{index}", string.Empty, 0, null);
    }

    public int Index { get; }
    public string Id { get; internal set; }
    public PgnMoveNode Root { get; }
    public IReadOnlyList<PgnHeader> Headers => _headers;
    public string Result { get; internal set; } = "*";
    public bool MovetextStarted { get; internal set; }
    public int NodeCount => Root.Descendants().Count();

    public string? Header(string name) =>
        _headers.LastOrDefault(header => string.Equals(header.Name, name, StringComparison.OrdinalIgnoreCase))?.Value;

    internal void AddHeader(PgnHeader header) => _headers.Add(header);

    internal void EnsureIdentity()
    {
        if (!Id.StartsWith("game_pending_", StringComparison.Ordinal))
        {
            return;
        }

        var headerIdentity = string.Join("\u001e", _headers.Select(static header => $"{header.Name}={header.Value}"));
        Id = StableId.Create("game", Index, headerIdentity);
        Root.StableId = StableId.Create("node", Id, "root");
    }
}

public sealed class PgnDocument
{
    internal PgnDocument(string sourceText, IReadOnlyList<PgnToken> tokens, IReadOnlyList<PgnGame> games, IReadOnlyList<PgnDiagnostic> diagnostics)
    {
        SourceText = sourceText;
        Tokens = tokens;
        Games = games;
        Diagnostics = diagnostics;
    }

    public string SourceText { get; }
    public IReadOnlyList<PgnToken> Tokens { get; }
    public IReadOnlyList<PgnGame> Games { get; }
    public IReadOnlyList<PgnDiagnostic> Diagnostics { get; }
    public int NodeCount => Games.Sum(static game => game.NodeCount);

    public string Serialize() => string.Concat(Tokens.Select(static token => token.RawText));
}
