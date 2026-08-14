using ChessMentor.Chess;

namespace ChessMentor.Tests;

public sealed class PromotionPolicyTests
{
    [Theory]
    [InlineData('P', 7, true)]
    [InlineData('P', 6, false)]
    [InlineData('p', 0, true)]
    [InlineData('p', 1, false)]
    [InlineData('N', 7, false)]
    public void PromotionIsRequestedOnlyForAPawnOnItsLastRank(char piece, int rank, bool expected) =>
        Assert.Equal(expected, PromotionPolicy.IsRequired(piece, new Square(0, rank)));

    [Fact]
    public void PromotionChoicesMatchUci()
    {
        Assert.Equal(new[] { 'q', 'r', 'b', 'n' }, PromotionPolicy.Choices);
    }
}
