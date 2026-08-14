using ChessMentor.Pgn;

namespace ChessMentor.Tests;

public sealed class EmbeddedCommentMoveRepairTests
{
    [Fact]
    public void NumberedLegalLinesBecomeRealVariationsWithoutLosingProse()
    {
        var token = TestContext.Current.CancellationToken;
        const string source = """
            [Event "Embedded repair"]
            [Result "*"]

            {Intro 1. ♙e4 e5 2. f3, for example 2... c6 3. ♗b5 a6. Compare after 1. d4 d5, note.} *
            """;
        var document = new PgnParser().Parse(source, token);
        _ = new PgnSemanticEnricher().Enrich(document, token);

        var stats = new EmbeddedCommentMoveRepair().Repair(document, token);
        var root = Assert.Single(document.Games).Root;

        Assert.Equal(2, stats.SequencesRepaired);
        Assert.Equal(8, stats.MovesRepaired);
        Assert.Equal("Intro", Assert.Single(root.Comments).Text);
        var e4 = root.Children.Single(node => node.Uci == "e2e4");
        var e5 = e4.Children.Single(node => node.Uci == "e7e5");
        var nf3 = e5.Children.Single(node => node.Uci == "g1f3");
        var nc6 = nf3.Children.Single(node => node.Uci == "b8c6");
        var bb5 = nc6.Children.Single(node => node.Uci == "f1b5");
        var a6 = bb5.Children.Single(node => node.Uci == "a7a6");
        Assert.Contains("for example", PgnTreeEditor.CommentText(nf3));
        Assert.Contains("Compare after", PgnTreeEditor.CommentText(a6));
        var d4 = root.Children.Single(node => node.Uci == "d2d4");
        Assert.Contains("note", PgnTreeEditor.CommentText(d4.Children.Single(node => node.Uci == "d7d5")));

        var exported = PgnAstSerializer.SerializeGames(document.Games);
        Assert.Contains("1. e4", exported);
        Assert.Contains("(1. d4 d5", exported);
        Assert.DoesNotContain("♙e4", exported);
        Assert.DoesNotContain("f3", exported);
    }

    [Fact]
    public void IllegalOrSinglePlyTextRemainsAComment()
    {
        var token = TestContext.Current.CancellationToken;
        var document = new PgnParser().Parse("{Keep 1. e5 e4 and 1. e4 as text.} *", token);
        _ = new PgnSemanticEnricher().Enrich(document, token);

        var stats = new EmbeddedCommentMoveRepair().Repair(document, token);

        Assert.Equal(0, stats.MovesRepaired);
        var root = Assert.Single(document.Games).Root;
        Assert.Empty(root.Children);
        Assert.Contains("1. e5 e4", Assert.Single(root.Comments).Text);
        Assert.Contains("1. e4", Assert.Single(root.Comments).Text);
    }

    [Fact]
    public void RepairedAnnotationsAndBlackEllipsisArePreserved()
    {
        var token = TestContext.Current.CancellationToken;
        const string source = """
            [SetUp "1"]
            [FEN "rnbqkbnr/pppp1ppp/8/4p3/4P3/8/PPPP1PPP/RNBQKBNR w KQkq - 0 2"]
            [Result "*"]

            {2. Nf3! ...Nc6??} *
            """;
        var document = new PgnParser().Parse(source, token);
        _ = new PgnSemanticEnricher().Enrich(document, token);

        var stats = new EmbeddedCommentMoveRepair().Repair(document, token);
        var nf3 = Assert.Single(Assert.Single(document.Games).Root.Children);
        var nc6 = Assert.Single(nf3.Children);

        Assert.Equal(2, stats.MovesRepaired);
        Assert.Contains("!", nf3.Annotations);
        Assert.Contains(1, nf3.Nags);
        Assert.Contains("??", nc6.Annotations);
        Assert.Contains(4, nc6.Nags);
        Assert.True(nc6.ForceMoveNumber);
        Assert.Contains("2... Nc6", PgnAstSerializer.SerializeGames(document.Games));
    }
}
