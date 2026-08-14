namespace ChessMentor.Pgn;

public enum PgnTokenKind
{
    Whitespace,
    Header,
    BraceComment,
    LineComment,
    VariationStart,
    VariationEnd,
    MoveNumber,
    Nag,
    Annotation,
    Result,
    Symbol,
}

public sealed class PgnToken
{
    internal PgnToken(PgnTokenKind kind, string rawText, int offset, int line, int column)
    {
        Kind = kind;
        RawText = rawText;
        Offset = offset;
        Line = line;
        Column = column;
    }

    public PgnTokenKind Kind { get; }
    public string RawText { get; private set; }
    public int Offset { get; }
    public int Line { get; }
    public int Column { get; }

    internal void ReplaceRawText(string value) => RawText = value;

    public override string ToString() => RawText;
}

public sealed record PgnDiagnostic(string Code, string Message, int Offset, int Line, int Column);
