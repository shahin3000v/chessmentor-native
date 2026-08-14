namespace ChessMentor.Chess;

public sealed class FenPosition
{
    public const string Initial = "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1";
    public const string Empty = "8/8/8/8/8/8/8/8 w - - 0 1";
    private static readonly HashSet<char> ValidPieces = [.. "KQRBNPkqrbnp"];
    private readonly char?[] _squares;

    private FenPosition(string source, char?[] squares, bool whiteToMove)
    {
        Source = source;
        _squares = squares;
        WhiteToMove = whiteToMove;
    }

    public string Source { get; }
    public bool WhiteToMove { get; }
    public int PieceCount => _squares.Count(static piece => piece.HasValue);

    public char? this[Square square] => _squares[square.Index];

    public static FenPosition Parse(string? fen)
    {
        var value = string.IsNullOrWhiteSpace(fen) ? Initial : fen.Trim();
        var sections = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (sections.Length < 2 || sections[1] is not ("w" or "b"))
        {
            throw new FormatException("FEN must contain piece placement and side to move.");
        }

        var ranks = sections[0].Split('/');
        if (ranks.Length != 8)
        {
            throw new FormatException("FEN piece placement must contain exactly eight ranks.");
        }

        var squares = new char?[64];
        for (var fenRank = 0; fenRank < 8; fenRank++)
        {
            var file = 0;
            foreach (var symbol in ranks[fenRank])
            {
                if (symbol is >= '1' and <= '8')
                {
                    file += symbol - '0';
                    continue;
                }

                if (!ValidPieces.Contains(symbol) || file >= 8)
                {
                    throw new FormatException($"Invalid FEN piece placement at rank {8 - fenRank}.");
                }

                var rank = 7 - fenRank;
                squares[new Square(file, rank).Index] = symbol;
                file++;
            }

            if (file != 8)
            {
                throw new FormatException($"FEN rank {8 - fenRank} expands to {file} squares instead of 8.");
            }
        }

        return new FenPosition(value, squares, sections[1] == "w");
    }

    public IEnumerable<(Square Square, char Piece)> Pieces()
    {
        for (var index = 0; index < _squares.Length; index++)
        {
            if (_squares[index] is { } piece)
            {
                yield return (new Square(index % 8, index / 8), piece);
            }
        }
    }
}
