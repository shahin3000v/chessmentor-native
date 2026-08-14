namespace ChessMentor.Chess;

public sealed class BoardInteractionState(double dragThreshold = 5d)
{
    private Square? _pressedSquare;
    private bool _pressedPiece;
    private double _startX;
    private double _startY;

    public Square? SelectedSquare { get; private set; }
    public bool IsDragging { get; private set; }
    public Square? DragSource => IsDragging ? _pressedSquare : null;

    public void PointerDown(Square square, bool hasPiece, double x, double y)
    {
        _pressedSquare = square;
        _pressedPiece = hasPiece;
        _startX = x;
        _startY = y;
        IsDragging = false;
    }

    public bool PointerMove(double x, double y)
    {
        if (!_pressedPiece || _pressedSquare is null || IsDragging)
        {
            return IsDragging;
        }

        var deltaX = x - _startX;
        var deltaY = y - _startY;
        IsDragging = Math.Sqrt((deltaX * deltaX) + (deltaY * deltaY)) >= dragThreshold;
        return IsDragging;
    }

    public BoardInteractionResult PointerUp(Square? target)
    {
        var source = _pressedSquare;
        var dragged = IsDragging;
        var hadPiece = _pressedPiece;
        ResetPress();

        if (dragged && source is not null && target is not null && source != target)
        {
            SelectedSquare = null;
            return new BoardInteractionResult(source, target, true);
        }

        if (SelectedSquare is not null && target is not null && SelectedSquare != target)
        {
            var selected = SelectedSquare;
            SelectedSquare = null;
            return new BoardInteractionResult(selected, target, false);
        }

        if (hadPiece && source is not null)
        {
            SelectedSquare = SelectedSquare == source ? null : source;
        }

        return BoardInteractionResult.None;
    }

    public void Cancel()
    {
        ResetPress();
        SelectedSquare = null;
    }

    private void ResetPress()
    {
        _pressedSquare = null;
        _pressedPiece = false;
        IsDragging = false;
    }
}

public readonly record struct BoardInteractionResult(Square? From, Square? To, bool WasDrag)
{
    public static BoardInteractionResult None => new(null, null, false);
    public bool HasMove => From is not null && To is not null;
}
