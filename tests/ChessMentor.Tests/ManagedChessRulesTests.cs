using ChessMentor.Chess;

namespace ChessMentor.Tests;

public sealed class ManagedChessRulesTests
{
    private readonly ManagedChessRules _rules = new();

    [Fact]
    public void InitialPositionHasTwentyLegalMovesAndResolvesE4()
    {
        var token = TestContext.Current.CancellationToken;
        var moves = _rules.GetLegalMoves(FenPosition.Initial, token);

        Assert.Equal(20, moves.Count);
        var e4 = Assert.Single(moves, static move => move.Uci == "e2e4");
        Assert.Equal("e4", e4.San);

        var resolved = _rules.ResolveSan(FenPosition.Initial, "e4", token);
        Assert.Equal("e2e4", resolved.Move.Uci);
        Assert.Equal("rnbqkbnr/pppppppp/8/8/4P3/8/PPPP1PPP/RNBQKBNR b KQkq - 0 1", resolved.Fen);
    }

    [Fact]
    public void CastlingEnPassantPromotionAndDisambiguationAreLegal()
    {
        var token = TestContext.Current.CancellationToken;
        const string castleFen = "r3k2r/8/8/8/8/8/8/R3K2R w KQkq - 0 1";
        var castles = _rules.GetLegalMoves(castleFen, token);
        Assert.Contains(castles, static move => move.Uci == "e1g1" && move.San == "O-O");
        Assert.Contains(castles, static move => move.Uci == "e1c1" && move.San == "O-O-O");

        const string enPassantFen = "4k3/8/8/3pP3/8/8/8/4K3 w - d6 0 2";
        var enPassant = Assert.Single(
            _rules.GetLegalMoves(enPassantFen, token),
            static move => move.Uci == "e5d6");
        Assert.Equal("exd6", enPassant.San);
        Assert.StartsWith("4k3/8/3P4/8/8/8/8/4K3 b", _rules.ApplyMove(enPassantFen, enPassant.Uci, token));

        const string promotionFen = "4k3/P7/8/8/8/8/8/4K3 w - - 0 1";
        var promotions = _rules.GetLegalMoves(promotionFen, token).Where(static move => move.Uci.StartsWith("a7a8", StringComparison.Ordinal)).ToArray();
        Assert.Equal(4, promotions.Length);
        Assert.Contains(promotions, static move => move.Uci == "a7a8q" && move.San.StartsWith("a8=Q", StringComparison.Ordinal));

        const string disambiguationFen = "4k3/8/8/8/8/2N1N3/8/4K3 w - - 0 1";
        var ambiguous = _rules.GetLegalMoves(disambiguationFen, token).Where(static move => move.To.Name == "d5").ToArray();
        Assert.Contains(ambiguous, static move => move.San == "Ncd5");
        Assert.Contains(ambiguous, static move => move.San == "Ned5");
    }

    [Fact]
    public void PinnedPieceCannotExposeItsKing()
    {
        var moves = _rules.GetLegalMoves(
            "4r1k1/8/8/8/8/8/4R3/4K3 w - - 0 1",
            TestContext.Current.CancellationToken);

        Assert.DoesNotContain(moves, static move => move.From.Name == "e2" && move.To.File != 4);

        const string pinnedEnPassant = "4r1k1/8/8/3pP3/8/8/8/4K3 w - d6 0 2";
        Assert.DoesNotContain(
            _rules.GetLegalMoves(pinnedEnPassant, TestContext.Current.CancellationToken),
            static move => move.Uci == "e5d6");
        Assert.EndsWith(" w - -", ManagedChessRules.PositionKey(pinnedEnPassant));
    }

    [Fact]
    public void StandardPerftFixturesRemainDeterministic()
    {
        var token = TestContext.Current.CancellationToken;
        Assert.Equal(20L, _rules.Perft(FenPosition.Initial, 1, token));
        Assert.Equal(400L, _rules.Perft(FenPosition.Initial, 2, token));
        Assert.Equal(8902L, _rules.Perft(FenPosition.Initial, 3, token));

        const string kiwipete = "r3k2r/p1ppqpb1/bn2pnp1/2pP4/1p2P3/2N2N2/PPQBBPPP/R3K2R w KQkq - 0 1";
        Assert.Equal(48L, _rules.Perft(kiwipete, 1, token));
        Assert.Equal(2039L, _rules.Perft(kiwipete, 2, token));
    }
}
