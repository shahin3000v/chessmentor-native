namespace ChessMentor.Chess;

public sealed record LegalMove(string Uci, string San, Square From, Square To, char? Promotion = null);

public interface IChessRules
{
    ValueTask<IReadOnlyList<LegalMove>> GetLegalMovesAsync(string fen, CancellationToken cancellationToken);
    ValueTask<string> ApplyMoveAsync(string fen, string uci, CancellationToken cancellationToken);
}
