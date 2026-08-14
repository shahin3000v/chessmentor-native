using ChessMentor.Pgn;
using ChessMentor.Viewer;

namespace ChessMentor.Tests;

public sealed class ViewerSessionTests
{
    [Fact]
    public void ViewerOneNavigationRequiresExplicitBranchChoice()
    {
        var session = new ViewerSession();
        session.Replace([Load("one.pgn", "1. e4 e5 (1... c5 {Sicilian}) 2. Nf3 *")]);

        Assert.Equal("e4", MoveNext(session));
        Assert.Equal(ViewerNavigationResult.BranchSelectionRequired, session.NextMove());
        Assert.True(session.IsBranchChooserOpen);
        Assert.Equal(new[] { "e5", "c5" }, session.Branches.Select(static branch => branch.Node.RawSan));

        Assert.True(session.SelectBranch(1));
        Assert.Equal("c5", session.CurrentNode?.RawSan);
        Assert.False(session.IsBranchChooserOpen);
        Assert.True(session.PreviousMove());
        Assert.Equal("e4", session.CurrentNode?.RawSan);
    }

    [Fact]
    public void AppendPreservesCurrentGameAndNodeReferences()
    {
        var session = new ViewerSession();
        session.Replace([Load("one.pgn", "1. e4 e5 *")]);
        MoveNext(session);
        var game = session.ActiveGame;
        var node = session.CurrentNode;

        session.Append([Load("two.pgn", "1. d4 d5 *")]);

        Assert.Equal(2, session.Games.Count);
        Assert.Same(game, session.ActiveGame);
        Assert.Same(node, session.CurrentNode);
    }

    [Fact]
    public void FlatMoveRowsKeepVariationsAndBlackNumbering()
    {
        var session = new ViewerSession();
        session.Replace([Load("tree.pgn", "1. e4 e5 (1... c5 $5 {Sicilian}) 2. Nf3 *")]);
        var rows = Assert.Single(session.Games).MoveItems;

        Assert.Equal(new[] { "e4", "e5", "c5", "Nf3" }, rows.Select(static row => row.San));
        var c5 = rows.Single(static row => row.San == "c5");
        Assert.True(c5.StartsVariation);
        Assert.Equal("1...", c5.MoveNumber);
        Assert.Equal("$5", c5.NagText);
        Assert.Equal("Sicilian", c5.Comment);
    }

    [Fact]
    public void StandardScoreRowsPairWhiteAndBlackInFixedColumns()
    {
        var session = new ViewerSession();
        session.Replace([Load("rows.pgn", "1. e4 e5 2. Nf3 Nc6 *")]);

        var rows = Assert.Single(session.Games).MoveRows;

        Assert.Equal(2, rows.Count);
        Assert.Equal("e4", rows[0].WhiteMove?.San);
        Assert.Equal("e5", rows[0].BlackMove?.San);
        Assert.Equal("Nf3", rows[1].WhiteMove?.San);
        Assert.Equal("Nc6", rows[1].BlackMove?.San);
    }

    [Fact]
    public void WhiteCommentForcesBlackOntoNextVisualLine()
    {
        var session = new ViewerSession();
        session.Replace([Load("comment.pgn", "1. e4 {White explanation} e5 *")]);

        var row = Assert.Single(Assert.Single(session.Games).MoveRows);

        Assert.Equal("e4", row.WhiteMove?.San);
        Assert.Equal("e5", row.BlackMove?.San);
        Assert.True(row.BlackOnNextLine);
    }

    [Fact]
    public void RemovingOneOfThreeGamesRemovesExactlyOne()
    {
        var session = new ViewerSession();
        session.Replace(
        [
            Load("one.pgn", "[White \"A\"]\n[Black \"B\"]\n\n1. e4 *"),
            Load("two.pgn", "[White \"C\"]\n[Black \"D\"]\n\n1. d4 *"),
            Load("three.pgn", "[White \"E\"]\n[Black \"F\"]\n\n1. c4 *"),
        ]);

        Assert.True(session.Remove(session.Games[1]));
        Assert.Equal(new[] { "A", "E" }, session.Games.Select(static game => game.White));
        Assert.Equal(new[] { 0, 1 }, session.Games.Select(static game => game.Index));
    }

    [Fact]
    public void CommentDigitsAreRenderedAsLatinWithoutChangingPgn()
    {
        const string text = "حرکت ۱۲ و زمان ٣ دقیقه";
        Assert.Equal("حرکت 12 و زمان 3 دقیقه", ViewerText.NormalizeCommentForDisplay(text));
    }

    [Fact]
    public void FenBlackStartUsesHeaderFullmoveAndEllipsis()
    {
        var session = new ViewerSession();
        session.Replace(
        [
            Load(
                "black.pgn",
                "[SetUp \"1\"]\n[FEN \"8/8/8/8/8/8/4k3/4K3 b - - 0 19\"]\n\n19... Kf3 *"),
        ]);

        var row = Assert.Single(Assert.Single(session.Games).MoveItems);
        Assert.False(row.IsWhiteMove);
        Assert.Equal(19, row.FullmoveNumber);
        Assert.Equal("19...", row.MoveNumber);
    }

    private static string MoveNext(ViewerSession session)
    {
        Assert.Equal(ViewerNavigationResult.Moved, session.NextMove());
        return Assert.IsType<string>(session.CurrentNode?.RawSan);
    }

    private static LoadedPgnSource Load(string name, string source)
    {
        var token = TestContext.Current.CancellationToken;
        var document = new PgnParser().Parse(source, token);
        var semantic = new PgnSemanticEnricher().Enrich(document, token);
        return new LoadedPgnSource(name, name, document, semantic);
    }
}
