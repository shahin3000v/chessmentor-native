namespace ChessMentor.Chess;

public readonly record struct Square
{
    public Square(int file, int rank)
    {
        if (file is < 0 or > 7 || rank is < 0 or > 7)
        {
            throw new ArgumentOutOfRangeException(nameof(file), "Chess square coordinates must be in 0..7.");
        }

        File = file;
        Rank = rank;
    }

    public int File { get; }
    public int Rank { get; }
    public int Index => Rank * 8 + File;
    public string Name => $"{(char)('a' + File)}{Rank + 1}";

    public static bool TryParse(string? value, out Square square)
    {
        if (value is { Length: 2 } && value[0] is >= 'a' and <= 'h' && value[1] is >= '1' and <= '8')
        {
            square = new Square(value[0] - 'a', value[1] - '1');
            return true;
        }

        square = default;
        return false;
    }

    public override string ToString() => Name;
}
