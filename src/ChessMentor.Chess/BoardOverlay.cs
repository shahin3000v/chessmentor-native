namespace ChessMentor.Chess;

public sealed record BoardArrow(Square From, Square To, uint Argb = 0xB4B42318, double Thickness = 0.12);
public sealed record BoardCircle(Square Square, uint Argb = 0xB4B42318, double Thickness = 0.10);

public sealed record BoardOverlay(
    IReadOnlyList<Square>? HighlightedSquares = null,
    IReadOnlyList<BoardArrow>? Arrows = null,
    IReadOnlyList<BoardCircle>? Circles = null,
    Square? LastMoveFrom = null,
    Square? LastMoveTo = null);
