using ChessMentor.Pgn;

namespace ChessMentor.Tests;

public sealed class PgnParserTests
{
    private readonly PgnParser _parser = new();

    [Fact]
    public void NestedVariationsCommentsAndNagsRemainStructured()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        const string source = """
            [Event "Nested"]
            [Result "1-0"]

            1. e4 {main note} e5 (1... {start c5} c5 $5 {Sicilian} (1... e6!?)) 2. Nf3 Nc6 1-0
            """;

        var document = _parser.Parse(source, cancellationToken);
        var game = Assert.Single(document.Games);
        var e4 = Assert.Single(game.Root.Children);

        Assert.Equal("e4", e4.RawSan);
        Assert.Equal("main note", Assert.Single(e4.Comments).Text);
        Assert.Equal(3, e4.Children.Count);
        var c5 = e4.Children.Single(node => node.RawSan == "c5");
        Assert.Equal("start c5", Assert.Single(c5.StartingComments).Text);
        Assert.Equal("Sicilian", Assert.Single(c5.Comments).Text);
        Assert.Contains(5, c5.Nags);
        var e6 = e4.Children.Single(node => node.RawSan == "e6");
        Assert.Contains("!?", e6.Annotations);
        Assert.Contains(5, e6.Nags);
        Assert.Empty(document.Diagnostics);
    }

    [Fact]
    public void ExactRoundTripPreservesWhitespaceEscapesCommentsNagsAndBlackNumbering()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        const string source = "[Event \"A \\\"quote\\\"\"]\r\n[Result \"*\"]\r\n\r\n19... Kf3 $14 {فارسی} ;line note\r\n20. Kf1 *\r\n";

        var first = _parser.Parse(source, cancellationToken);
        var serialized = PgnSerializer.Serialize(first);
        var second = _parser.Parse(serialized, cancellationToken);

        Assert.Equal(source, serialized);
        Assert.Equal(source, PgnSerializer.Serialize(second));
        Assert.Contains(first.Tokens, token => token.Kind == PgnTokenKind.MoveNumber && token.RawText == "19...");
        Assert.Contains(first.Games[0].Root.Descendants().First().Nags, nag => nag == 14);
    }

    [Fact]
    public void MultiGameDocumentDoesNotMergeGames()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        const string source = """
            [Event "One"]
            [Result "1-0"]
            1. e4 e5 1-0

            [Event "Two"]
            [Result "0-1"]
            1. d4 d5 0-1
            """;

        var document = _parser.Parse(source, cancellationToken);

        Assert.Equal(2, document.Games.Count);
        Assert.Equal("One", document.Games[0].Header("Event"));
        Assert.Equal("Two", document.Games[1].Header("Event"));
        Assert.Equal(4, document.NodeCount);
    }

    [Fact]
    public void EditingACommentChangesOnlyItsTokenAndReparses()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        const string source = "1. e4 {old} e5 *";
        var document = _parser.Parse(source, cancellationToken);
        var e4 = document.Games[0].Root.Children[0];

        Assert.Single(e4.Comments).Text = "متن جدید";
        var edited = document.Serialize();
        var reparsed = _parser.Parse(edited, cancellationToken);

        Assert.Equal("1. e4 {متن جدید} e5 *", edited);
        Assert.Equal("متن جدید", Assert.Single(reparsed.Games[0].Root.Children[0].Comments).Text);
    }

    [Fact]
    public void StableNodeIdsAreDeterministicAcrossParses()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        const string source = "[Event \"IDs\"]\n1. e4 e5 (1... c5) 2. Nf3 *";

        var first = _parser.Parse(source, cancellationToken).Games[0].Root.Descendants().Select(node => node.StableId).ToArray();
        var second = _parser.Parse(source, cancellationToken).Games[0].Root.Descendants().Select(node => node.StableId).ToArray();

        Assert.Equal(first, second);
        Assert.Equal(first.Length, first.Distinct(StringComparer.Ordinal).Count());
    }
}
