using ChessMentor.Chess;

namespace ChessMentor.Tests;

public sealed class BoardGeometryTests
{
    [Fact]
    public void Calculate_AlwaysProducesOneExactEightByEightSquareGrid()
    {
        var geometry = BoardGeometry.Calculate(1013, 733);

        Assert.Equal(733, geometry.Size);
        Assert.Equal(geometry.Size / 8d, geometry.SquareSize);
        Assert.Equal(64, BoardGeometry.SquareCount);
        Assert.Equal(geometry.Size, geometry.SquareSize * 8d, precision: 10);
        Assert.Equal((1013 - 733) / 2d, geometry.Left);
        Assert.Equal(0, geometry.Top);
    }

    [Theory]
    [InlineData(BoardOrientation.White, 0, 0, "a8")]
    [InlineData(BoardOrientation.White, 799, 799, "h1")]
    [InlineData(BoardOrientation.Black, 0, 0, "h1")]
    [InlineData(BoardOrientation.Black, 799, 799, "a8")]
    public void HitTest_RespectsOrientation(BoardOrientation orientation, double x, double y, string expected)
    {
        var geometry = BoardGeometry.Calculate(800, 800);

        Assert.Equal(expected, geometry.HitTest(x, y, orientation)?.Name);
    }

    [Fact]
    public void EverySquareHasTheSameDimensions()
    {
        var geometry = BoardGeometry.Calculate(640, 640);
        var positions = new HashSet<(double X, double Y)>();
        for (var rank = 0; rank < 8; rank++)
        {
            for (var file = 0; file < 8; file++)
            {
                positions.Add(geometry.TopLeft(new Square(file, rank), BoardOrientation.White));
            }
        }

        Assert.Equal(64, positions.Count);
        Assert.Equal(80, geometry.SquareSize);
    }
}
