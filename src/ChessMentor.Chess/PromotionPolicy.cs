namespace ChessMentor.Chess;

public static class PromotionPolicy
{
    public static IReadOnlyList<char> Choices { get; } = ['q', 'r', 'b', 'n'];

    public static bool IsRequired(char piece, Square target) =>
        (piece == 'P' && target.Rank == 7) || (piece == 'p' && target.Rank == 0);
}
