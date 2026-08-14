using ChessMentor.Core;

namespace ChessMentor.Tests;

public sealed class StableIdTests
{
    [Fact]
    public void SameIdentityAlwaysProducesSameCompactId()
    {
        var first = StableId.Create("node", "game", "parent", "e4", 0);
        var second = StableId.Create("node", "game", "parent", "e4", 0);

        Assert.Equal(first, second);
        Assert.StartsWith("node_", first);
        Assert.NotEqual(first, StableId.Create("node", "game", "parent", "e4", 1));
    }
}
