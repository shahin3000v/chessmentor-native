using ChessMentor.Pgn;

namespace ChessMentor.Tests;

public sealed class PgnSemanticEnricherTests
{
    [Fact]
    public void NestedVariationsReceiveUciFenAndTranspositionIdentity()
    {
        var token = TestContext.Current.CancellationToken;
        const string source = """
            [Event "Semantic"]
            [Result "*"]

            1. Nf3 d5 (1... Nf6 2. g3 d5) 2. g3 Nf6 *
            """;
        var document = new PgnParser().Parse(source, token);
        var result = new PgnSemanticEnricher().Enrich(document, token);

        Assert.True(result.IsComplete);
        Assert.Equal(document.NodeCount, result.ResolvedNodeCount);
        var game = Assert.Single(document.Games);
        var nf3 = Assert.Single(game.Root.Children);
        Assert.Equal("g1f3", nf3.Uci);
        Assert.NotNull(nf3.Fen);
        Assert.NotNull(nf3.PositionKey);
        Assert.NotNull(nf3.TranspositionGroupId);

        var positions = game.Root.Descendants()
            .Where(static node => node.PositionKey is not null)
            .GroupBy(static node => node.PositionKey, StringComparer.Ordinal)
            .Where(static group => group.Count() > 1)
            .ToArray();
        Assert.NotEmpty(positions);
        Assert.All(positions, group => Assert.Single(group.Select(static node => node.TranspositionGroupId).Distinct(StringComparer.Ordinal)));
    }

    [Fact]
    public void InvalidSanIsReportedWithoutChangingLosslessSource()
    {
        var token = TestContext.Current.CancellationToken;
        const string source = "[Event \"Broken\"]\n\n1. e4 e5 2. NotAMove *\n";
        var document = new PgnParser().Parse(source, token);
        var result = new PgnSemanticEnricher().Enrich(document, token);

        Assert.Equal(source, document.Serialize());
        Assert.Equal(1, result.UnresolvedNodeCount);
        Assert.Equal("NotAMove", Assert.Single(result.Diagnostics).San);
    }

    [Fact]
    public void BlackToMoveFenAndEllipsisResolveFromTheHeaderPosition()
    {
        var token = TestContext.Current.CancellationToken;
        const string source = """
            [SetUp "1"]
            [FEN "8/8/8/8/8/8/4k3/4K3 b - - 0 19"]
            [Result "*"]

            19... Kf3 *
            """;
        var document = new PgnParser().Parse(source, token);
        var result = new PgnSemanticEnricher().Enrich(document, token);
        var move = Assert.Single(Assert.Single(document.Games).Root.Children);

        Assert.True(result.IsComplete);
        Assert.Equal("e2f3", move.Uci);
        Assert.Equal(1, move.Ply);
        Assert.StartsWith("8/8/8/8/8/5k2/8/4K3 w - - 1 20", move.Fen);
    }
}
