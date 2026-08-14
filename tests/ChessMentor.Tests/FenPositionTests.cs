using ChessMentor.Chess;

namespace ChessMentor.Tests;

public sealed class FenPositionTests
{
    [Fact]
    public void EmptyRanksNeverCollapse()
    {
        var position = FenPosition.Parse("8/8/8/8/8/8/4k3/4K3 w - - 0 1");

        Assert.Equal(2, position.PieceCount);
        Assert.Null(position[new Square(0, 7)]);
        Assert.Equal('k', position[new Square(4, 1)]);
        Assert.Equal('K', position[new Square(4, 0)]);
    }

    [Theory]
    [InlineData("8/8/8/8/8/8/8 w - - 0 1")]
    [InlineData("9/8/8/8/8/8/8/8 w - - 0 1")]
    [InlineData("8/8/8/8/8/8/8/7X w - - 0 1")]
    public void InvalidPlacementIsRejected(string fen) =>
        Assert.Throws<FormatException>(() => FenPosition.Parse(fen));
}
