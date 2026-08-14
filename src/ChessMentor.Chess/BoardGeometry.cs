namespace ChessMentor.Chess;

public readonly record struct BoardGeometry(double Left, double Top, double Size, double SquareSize)
{
    public const int Files = 8;
    public const int Ranks = 8;
    public const int SquareCount = Files * Ranks;

    public static BoardGeometry Calculate(double availableWidth, double availableHeight)
    {
        if (!double.IsFinite(availableWidth) || !double.IsFinite(availableHeight) ||
            availableWidth <= 0 || availableHeight <= 0)
        {
            return default;
        }

        var size = Math.Min(availableWidth, availableHeight);
        var square = size / Files;
        return new BoardGeometry(
            (availableWidth - size) / 2d,
            (availableHeight - size) / 2d,
            size,
            square);
    }

    public Square? HitTest(double x, double y, BoardOrientation orientation)
    {
        if (SquareSize <= 0 || x < Left || y < Top || x >= Left + Size || y >= Top + Size)
        {
            return null;
        }

        var visualFile = Math.Clamp((int)((x - Left) / SquareSize), 0, 7);
        var visualRank = Math.Clamp((int)((y - Top) / SquareSize), 0, 7);
        var file = orientation == BoardOrientation.White ? visualFile : 7 - visualFile;
        var rank = orientation == BoardOrientation.White ? 7 - visualRank : visualRank;
        return new Square(file, rank);
    }

    public (double X, double Y) TopLeft(Square square, BoardOrientation orientation)
    {
        var visualFile = orientation == BoardOrientation.White ? square.File : 7 - square.File;
        var visualRank = orientation == BoardOrientation.White ? 7 - square.Rank : square.Rank;
        return (Left + visualFile * SquareSize, Top + visualRank * SquareSize);
    }
}
