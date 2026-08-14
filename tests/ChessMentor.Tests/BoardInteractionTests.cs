using ChessMentor.Chess;

namespace ChessMentor.Tests;

public sealed class BoardInteractionTests
{
    [Fact]
    public void EmptySquareCanNeverStartAPieceDrag()
    {
        var state = new BoardInteractionState(dragThreshold: 4);
        state.PointerDown(new Square(4, 3), hasPiece: false, 10, 10);

        Assert.False(state.PointerMove(100, 100));
        Assert.False(state.IsDragging);
        Assert.Null(state.DragSource);
    }

    [Fact]
    public void DragMovesOnlyThePressedPiece()
    {
        var state = new BoardInteractionState(dragThreshold: 4);
        var from = new Square(4, 1);
        var to = new Square(4, 3);
        state.PointerDown(from, hasPiece: true, 10, 10);

        Assert.True(state.PointerMove(20, 20));
        Assert.Equal(from, state.DragSource);
        var result = state.PointerUp(to);

        Assert.True(result.HasMove);
        Assert.True(result.WasDrag);
        Assert.Equal(from, result.From);
        Assert.Equal(to, result.To);
    }

    [Fact]
    public void ClickToMoveWorksForEmptyOrOccupiedTargets()
    {
        var state = new BoardInteractionState();
        var from = new Square(4, 1);
        var target = new Square(4, 3);
        state.PointerDown(from, hasPiece: true, 10, 10);
        Assert.False(state.PointerUp(from).HasMove);

        state.PointerDown(target, hasPiece: true, 10, 10);
        var result = state.PointerUp(target);

        Assert.True(result.HasMove);
        Assert.False(result.WasDrag);
        Assert.Equal(from, result.From);
        Assert.Equal(target, result.To);
    }
}
