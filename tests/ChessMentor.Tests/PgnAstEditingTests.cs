using ChessMentor.Chess;
using ChessMentor.Pgn;

namespace ChessMentor.Tests;

public sealed class PgnAstEditingTests
{
    [Fact]
    public void AuthoredCommentAndNestedVariationSurviveReparse()
    {
        var token = TestContext.Current.CancellationToken;
        const string source = "[Event \"Studio\"]\n[Result \"*\"]\n\n1. e4 e5 (1... c5 !? {Sicilian}) 2. Nf3 *";
        var document = new PgnParser().Parse(source, token);
        _ = new PgnSemanticEnricher().Enrich(document, token);
        var game = Assert.Single(document.Games);
        var e4 = Assert.Single(game.Root.Children);
        var e5 = e4.Children[0];

        PgnTreeEditor.SetComment(e5, "متن ویرایش‌شده");
        var rules = ManagedChessRules.Instance;
        var e6 = rules.GetLegalMoves(e4.Fen!, token).Single(move => move.Uci == "e7e6");
        var insert = PgnTreeEditor.AddMove(game, e4, e6, rules.ApplyMove(e4.Fen!, e6.Uci, token));
        PgnTreeEditor.SetComment(insert.Node, "شاخه تازه");

        var serialized = PgnAstSerializer.SerializeGames([game]);
        var reparsed = new PgnParser().Parse(serialized, token);
        _ = new PgnSemanticEnricher().Enrich(reparsed, token);
        var reparsedE4 = Assert.Single(Assert.Single(reparsed.Games).Root.Children);

        Assert.Equal(["e7e5", "c7c5", "e7e6"], reparsedE4.Children.Select(static node => node.Uci));
        Assert.Equal("متن ویرایش‌شده", Assert.Single(reparsedE4.Children[0].Comments).Text);
        Assert.Contains(5, reparsedE4.Children[1].Nags);
        Assert.Contains("!?", reparsedE4.Children[1].Annotations);
        Assert.Equal("شاخه تازه", Assert.Single(reparsedE4.Children[2].Comments).Text);
    }

    [Fact]
    public void DeleteRemovesBranchAndPromotePreservesFormerMainline()
    {
        var token = TestContext.Current.CancellationToken;
        var document = new PgnParser().Parse("1. e4 e5 (1... c5 2. Nf3) (1... e6) *", token);
        _ = new PgnSemanticEnricher().Enrich(document, token);
        var e4 = Assert.Single(Assert.Single(document.Games).Root.Children);
        var c5 = e4.Children.Single(node => node.Uci == "c7c5");
        var e6 = e4.Children.Single(node => node.Uci == "e7e6");

        Assert.True(PgnTreeEditor.DeleteBranch(c5));
        Assert.DoesNotContain(e4.Children, node => node.Uci == "c7c5");
        Assert.True(PgnTreeEditor.PromoteToMainline(e6));
        Assert.Same(e6, e4.Children[0]);

        var serialized = PgnAstSerializer.SerializeGames(document.Games);
        var reparsed = new PgnParser().Parse(serialized, token);
        _ = new PgnSemanticEnricher().Enrich(reparsed, token);
        var reparsedE4 = Assert.Single(Assert.Single(reparsed.Games).Root.Children);
        Assert.Equal(["e7e6", "e7e5"], reparsedE4.Children.Select(static node => node.Uci));
        Assert.DoesNotContain(reparsedE4.Children, static node => node.Uci == "c7c5");
    }
}
