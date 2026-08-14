using System.Text;
using System.Text.RegularExpressions;

namespace ChessMentor.Chess;

/// <summary>
/// Fully managed chess-rules adapter used by native viewers and trainers.
/// It deliberately has no UI or PGN dependency and is safe to run on worker threads.
/// </summary>
public sealed partial class ManagedChessRules : IChessRules
{
    public static ManagedChessRules Instance { get; } = new();

    public ValueTask<IReadOnlyList<LegalMove>> GetLegalMovesAsync(
        string fen,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(GetLegalMoves(fen, cancellationToken));
    }

    public ValueTask<string> ApplyMoveAsync(string fen, string uci, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(ApplyMove(fen, uci, cancellationToken));
    }

    public IReadOnlyList<LegalMove> GetLegalMoves(string fen, CancellationToken cancellationToken = default)
    {
        var position = PositionState.Parse(fen);
        var generated = GenerateLegalMoves(position, cancellationToken);
        var result = new LegalMove[generated.Count];
        for (var index = 0; index < generated.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var move = generated[index];
            result[index] = new LegalMove(
                move.Uci,
                FormatSan(position, move, generated, cancellationToken),
                move.From,
                move.To,
                move.Promotion);
        }

        return result;
    }

    public string ApplyMove(string fen, string uci, CancellationToken cancellationToken = default)
    {
        var position = PositionState.Parse(fen);
        var requested = NormalizeUci(uci);
        var move = GenerateLegalMoves(position, cancellationToken)
            .FirstOrDefault(candidate => string.Equals(candidate.Uci, requested, StringComparison.Ordinal));
        if (move == default)
        {
            throw new InvalidOperationException($"Illegal move '{uci}' for the supplied FEN.");
        }

        return ApplyUnchecked(position, move).ToFen();
    }

    public bool TryResolveSan(
        string fen,
        string san,
        out MoveResolution? resolution,
        CancellationToken cancellationToken = default)
    {
        var position = PositionState.Parse(fen);
        var legal = GenerateLegalMoves(position, cancellationToken);
        var normalizedInput = NormalizeSan(san);

        var normalizedUci = normalizedInput.ToLowerInvariant();
        if (UciRegex().IsMatch(normalizedUci))
        {
            var uciMove = legal.FirstOrDefault(move => string.Equals(move.Uci, normalizedUci, StringComparison.Ordinal));
            if (uciMove != default)
            {
                var uciSan = FormatSan(position, uciMove, legal, cancellationToken);
                resolution = CreateResolution(position, uciMove, uciSan);
                return true;
            }
        }

        foreach (var move in FilterSanCandidates(normalizedInput, legal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var generatedSan = FormatSan(position, move, legal, cancellationToken);
            if (!SanEquivalent(normalizedInput, NormalizeSan(generatedSan)))
            {
                continue;
            }

            resolution = CreateResolution(position, move, generatedSan);
            return true;
        }

        resolution = null;
        return false;
    }

    private static IEnumerable<GeneratedMove> FilterSanCandidates(
        string normalizedSan,
        IReadOnlyList<GeneratedMove> legal)
    {
        var withoutCheck = StripCheckSuffix(normalizedSan);
        if (withoutCheck is "O-O" or "O-O-O")
        {
            var required = withoutCheck == "O-O" ? MoveFlags.CastleKingSide : MoveFlags.CastleQueenSide;
            return legal.Where(move => move.Flags.HasFlag(required));
        }

        var match = SanTargetRegex().Match(withoutCheck);
        if (!match.Success || !Square.TryParse(match.Groups[1].Value, out var target))
        {
            return legal;
        }

        var piece = withoutCheck.Length > 0 && "KQRBN".Contains(withoutCheck[0])
            ? withoutCheck[0]
            : 'P';
        char? promotion = null;
        var promotionIndex = withoutCheck.LastIndexOf('=');
        if (promotionIndex >= 0 && promotionIndex + 1 < withoutCheck.Length)
        {
            promotion = char.ToLowerInvariant(withoutCheck[promotionIndex + 1]);
        }

        var capture = withoutCheck.Contains('x');
        return legal.Where(move =>
            move.To == target &&
            char.ToUpperInvariant(move.Piece) == piece &&
            move.Flags.HasFlag(MoveFlags.Capture) == capture &&
            move.Promotion == promotion);
    }

    private static MoveResolution CreateResolution(
        PositionState position,
        GeneratedMove move,
        string san)
    {
        var next = ApplyUnchecked(position, move);
        return new MoveResolution(
            new LegalMove(move.Uci, san, move.From, move.To, move.Promotion),
            next.ToFen(),
            next.PositionKey);
    }

    public MoveResolution ResolveSan(
        string fen,
        string san,
        CancellationToken cancellationToken = default) =>
        TryResolveSan(fen, san, out var resolution, cancellationToken)
            ? resolution!
            : throw new InvalidOperationException($"SAN '{san}' is not legal for the supplied FEN.");

    public static string PositionKey(string fen) => PositionState.Parse(fen).PositionKey;

    public long Perft(string fen, int depth, CancellationToken cancellationToken = default)
    {
        if (depth < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(depth));
        }

        return Perft(PositionState.Parse(fen), depth, cancellationToken);
    }

    private static long Perft(PositionState position, int depth, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (depth == 0)
        {
            return 1;
        }

        long nodes = 0;
        foreach (var move in GenerateLegalMoves(position, cancellationToken))
        {
            nodes += Perft(ApplyUnchecked(position, move), depth - 1, cancellationToken);
        }

        return nodes;
    }

    private static List<GeneratedMove> GenerateLegalMoves(
        PositionState position,
        CancellationToken cancellationToken)
    {
        var movingWhite = position.WhiteToMove;
        var pseudo = GeneratePseudoLegalMoves(position);
        var legal = new List<GeneratedMove>(pseudo.Count);
        foreach (var move in pseudo)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var next = ApplyUnchecked(position, move);
            if (!IsKingInCheck(next, movingWhite))
            {
                legal.Add(move);
            }
        }

        return legal;
    }

    private static List<GeneratedMove> GeneratePseudoLegalMoves(PositionState position)
    {
        var moves = new List<GeneratedMove>(48);
        for (var index = 0; index < 64; index++)
        {
            var piece = position.Board[index];
            if (piece is null || char.IsUpper(piece.Value) != position.WhiteToMove)
            {
                continue;
            }

            var from = new Square(index % 8, index / 8);
            switch (char.ToUpperInvariant(piece.Value))
            {
                case 'P':
                    AddPawnMoves(position, from, piece.Value, moves);
                    break;
                case 'N':
                    AddStepMoves(position, from, piece.Value, KnightOffsets, moves);
                    break;
                case 'B':
                    AddSlidingMoves(position, from, piece.Value, BishopDirections, moves);
                    break;
                case 'R':
                    AddSlidingMoves(position, from, piece.Value, RookDirections, moves);
                    break;
                case 'Q':
                    AddSlidingMoves(position, from, piece.Value, QueenDirections, moves);
                    break;
                case 'K':
                    AddStepMoves(position, from, piece.Value, KingOffsets, moves);
                    AddCastlingMoves(position, from, piece.Value, moves);
                    break;
            }
        }

        return moves;
    }

    private static void AddPawnMoves(
        PositionState position,
        Square from,
        char piece,
        List<GeneratedMove> moves)
    {
        var white = char.IsUpper(piece);
        var direction = white ? 1 : -1;
        var startRank = white ? 1 : 6;
        var promotionRank = white ? 7 : 0;
        var oneRank = from.Rank + direction;
        if (IsInside(from.File, oneRank))
        {
            var one = new Square(from.File, oneRank);
            if (position[one] is null)
            {
                AddPawnDestination(from, one, piece, null, promotionRank, MoveFlags.None, moves);
                var twoRank = from.Rank + (2 * direction);
                if (from.Rank == startRank && IsInside(from.File, twoRank))
                {
                    var two = new Square(from.File, twoRank);
                    if (position[two] is null)
                    {
                        moves.Add(new GeneratedMove(from, two, piece, null, null, MoveFlags.PawnDouble));
                    }
                }
            }
        }

        foreach (var fileDelta in new[] { -1, 1 })
        {
            var targetFile = from.File + fileDelta;
            var targetRank = from.Rank + direction;
            if (!IsInside(targetFile, targetRank))
            {
                continue;
            }

            var target = new Square(targetFile, targetRank);
            var captured = position[target];
            if (captured is not null && char.IsUpper(captured.Value) != white)
            {
                AddPawnDestination(from, target, piece, captured, promotionRank, MoveFlags.Capture, moves);
                continue;
            }

            if (position.EnPassant == target)
            {
                var capturedSquare = new Square(target.File, target.Rank - direction);
                var expectedPawn = white ? 'p' : 'P';
                if (position[capturedSquare] == expectedPawn)
                {
                    moves.Add(new GeneratedMove(
                        from,
                        target,
                        piece,
                        expectedPawn,
                        null,
                        MoveFlags.Capture | MoveFlags.EnPassant));
                }
            }
        }
    }

    private static void AddPawnDestination(
        Square from,
        Square to,
        char piece,
        char? captured,
        int promotionRank,
        MoveFlags flags,
        List<GeneratedMove> moves)
    {
        if (to.Rank != promotionRank)
        {
            moves.Add(new GeneratedMove(from, to, piece, captured, null, flags));
            return;
        }

        foreach (var promotion in PromotionPolicy.Choices)
        {
            moves.Add(new GeneratedMove(from, to, piece, captured, promotion, flags | MoveFlags.Promotion));
        }
    }

    private static void AddStepMoves(
        PositionState position,
        Square from,
        char piece,
        IReadOnlyList<(int File, int Rank)> offsets,
        List<GeneratedMove> moves)
    {
        var white = char.IsUpper(piece);
        foreach (var (fileOffset, rankOffset) in offsets)
        {
            var file = from.File + fileOffset;
            var rank = from.Rank + rankOffset;
            if (!IsInside(file, rank))
            {
                continue;
            }

            var target = new Square(file, rank);
            var captured = position[target];
            if (captured is not null && char.IsUpper(captured.Value) == white)
            {
                continue;
            }

            moves.Add(new GeneratedMove(
                from,
                target,
                piece,
                captured,
                null,
                captured is null ? MoveFlags.None : MoveFlags.Capture));
        }
    }

    private static void AddSlidingMoves(
        PositionState position,
        Square from,
        char piece,
        IReadOnlyList<(int File, int Rank)> directions,
        List<GeneratedMove> moves)
    {
        var white = char.IsUpper(piece);
        foreach (var (fileDirection, rankDirection) in directions)
        {
            var file = from.File + fileDirection;
            var rank = from.Rank + rankDirection;
            while (IsInside(file, rank))
            {
                var target = new Square(file, rank);
                var captured = position[target];
                if (captured is null)
                {
                    moves.Add(new GeneratedMove(from, target, piece, null, null, MoveFlags.None));
                }
                else
                {
                    if (char.IsUpper(captured.Value) != white)
                    {
                        moves.Add(new GeneratedMove(from, target, piece, captured, null, MoveFlags.Capture));
                    }

                    break;
                }

                file += fileDirection;
                rank += rankDirection;
            }
        }
    }

    private static void AddCastlingMoves(
        PositionState position,
        Square from,
        char king,
        List<GeneratedMove> moves)
    {
        var white = char.IsUpper(king);
        var rank = white ? 0 : 7;
        if (from != new Square(4, rank) || IsKingInCheck(position, white))
        {
            return;
        }

        var opponentWhite = !white;
        var kingSideRight = white ? 'K' : 'k';
        if (position.CastlingRights.Contains(kingSideRight) &&
            position[new Square(7, rank)] == (white ? 'R' : 'r') &&
            position[new Square(5, rank)] is null &&
            position[new Square(6, rank)] is null &&
            !IsSquareAttacked(position, new Square(5, rank), opponentWhite) &&
            !IsSquareAttacked(position, new Square(6, rank), opponentWhite))
        {
            moves.Add(new GeneratedMove(
                from,
                new Square(6, rank),
                king,
                null,
                null,
                MoveFlags.CastleKingSide));
        }

        var queenSideRight = white ? 'Q' : 'q';
        if (position.CastlingRights.Contains(queenSideRight) &&
            position[new Square(0, rank)] == (white ? 'R' : 'r') &&
            position[new Square(1, rank)] is null &&
            position[new Square(2, rank)] is null &&
            position[new Square(3, rank)] is null &&
            !IsSquareAttacked(position, new Square(3, rank), opponentWhite) &&
            !IsSquareAttacked(position, new Square(2, rank), opponentWhite))
        {
            moves.Add(new GeneratedMove(
                from,
                new Square(2, rank),
                king,
                null,
                null,
                MoveFlags.CastleQueenSide));
        }
    }

    private static PositionState ApplyUnchecked(PositionState current, GeneratedMove move)
    {
        var next = current.Clone();
        next.Board[move.From.Index] = null;
        if (move.Flags.HasFlag(MoveFlags.EnPassant))
        {
            var direction = char.IsUpper(move.Piece) ? 1 : -1;
            next.Board[new Square(move.To.File, move.To.Rank - direction).Index] = null;
        }

        if (move.Flags.HasFlag(MoveFlags.CastleKingSide))
        {
            var rank = move.From.Rank;
            next.Board[new Square(7, rank).Index] = null;
            next.Board[new Square(5, rank).Index] = char.IsUpper(move.Piece) ? 'R' : 'r';
        }
        else if (move.Flags.HasFlag(MoveFlags.CastleQueenSide))
        {
            var rank = move.From.Rank;
            next.Board[new Square(0, rank).Index] = null;
            next.Board[new Square(3, rank).Index] = char.IsUpper(move.Piece) ? 'R' : 'r';
        }

        next.Board[move.To.Index] = move.Promotion is { } promotion
            ? (char.IsUpper(move.Piece) ? char.ToUpperInvariant(promotion) : char.ToLowerInvariant(promotion))
            : move.Piece;

        next.CastlingRights = UpdateCastlingRights(current.CastlingRights, move);
        next.EnPassant = move.Flags.HasFlag(MoveFlags.PawnDouble)
            ? new Square(move.From.File, (move.From.Rank + move.To.Rank) / 2)
            : null;
        next.HalfmoveClock = char.ToUpperInvariant(move.Piece) == 'P' || move.Flags.HasFlag(MoveFlags.Capture)
            ? 0
            : current.HalfmoveClock + 1;
        next.FullmoveNumber = current.FullmoveNumber + (current.WhiteToMove ? 0 : 1);
        next.WhiteToMove = !current.WhiteToMove;
        return next;
    }

    private static string UpdateCastlingRights(string current, GeneratedMove move)
    {
        var rights = current == "-" ? string.Empty : current;
        rights = move.Piece switch
        {
            'K' => rights.Replace("K", string.Empty, StringComparison.Ordinal)
                .Replace("Q", string.Empty, StringComparison.Ordinal),
            'k' => rights.Replace("k", string.Empty, StringComparison.Ordinal)
                .Replace("q", string.Empty, StringComparison.Ordinal),
            'R' when move.From == new Square(0, 0) => rights.Replace("Q", string.Empty, StringComparison.Ordinal),
            'R' when move.From == new Square(7, 0) => rights.Replace("K", string.Empty, StringComparison.Ordinal),
            'r' when move.From == new Square(0, 7) => rights.Replace("q", string.Empty, StringComparison.Ordinal),
            'r' when move.From == new Square(7, 7) => rights.Replace("k", string.Empty, StringComparison.Ordinal),
            _ => rights,
        };

        rights = move.To switch
        {
            { File: 0, Rank: 0 } when move.Captured == 'R' => rights.Replace("Q", string.Empty, StringComparison.Ordinal),
            { File: 7, Rank: 0 } when move.Captured == 'R' => rights.Replace("K", string.Empty, StringComparison.Ordinal),
            { File: 0, Rank: 7 } when move.Captured == 'r' => rights.Replace("q", string.Empty, StringComparison.Ordinal),
            { File: 7, Rank: 7 } when move.Captured == 'r' => rights.Replace("k", string.Empty, StringComparison.Ordinal),
            _ => rights,
        };
        return string.Concat("KQkq".Where(rights.Contains));
    }

    private static bool IsKingInCheck(PositionState position, bool whiteKing)
    {
        var king = whiteKing ? 'K' : 'k';
        var kingIndex = Array.IndexOf(position.Board, king);
        return kingIndex >= 0 && IsSquareAttacked(
            position,
            new Square(kingIndex % 8, kingIndex / 8),
            !whiteKing);
    }

    private static bool IsSquareAttacked(PositionState position, Square target, bool byWhite)
    {
        var pawnSourceRank = target.Rank + (byWhite ? -1 : 1);
        foreach (var fileDelta in new[] { -1, 1 })
        {
            var file = target.File + fileDelta;
            if (IsInside(file, pawnSourceRank) &&
                position[new Square(file, pawnSourceRank)] == (byWhite ? 'P' : 'p'))
            {
                return true;
            }
        }

        foreach (var (fileOffset, rankOffset) in KnightOffsets)
        {
            var file = target.File + fileOffset;
            var rank = target.Rank + rankOffset;
            if (IsInside(file, rank) && position[new Square(file, rank)] == (byWhite ? 'N' : 'n'))
            {
                return true;
            }
        }

        foreach (var (fileOffset, rankOffset) in KingOffsets)
        {
            var file = target.File + fileOffset;
            var rank = target.Rank + rankOffset;
            if (IsInside(file, rank) && position[new Square(file, rank)] == (byWhite ? 'K' : 'k'))
            {
                return true;
            }
        }

        return IsSlidingAttack(position, target, byWhite, BishopDirections, 'B') ||
               IsSlidingAttack(position, target, byWhite, RookDirections, 'R');
    }

    private static bool IsSlidingAttack(
        PositionState position,
        Square target,
        bool byWhite,
        IReadOnlyList<(int File, int Rank)> directions,
        char expectedPiece)
    {
        foreach (var (fileDirection, rankDirection) in directions)
        {
            var file = target.File + fileDirection;
            var rank = target.Rank + rankDirection;
            while (IsInside(file, rank))
            {
                var piece = position[new Square(file, rank)];
                if (piece is null)
                {
                    file += fileDirection;
                    rank += rankDirection;
                    continue;
                }

                if (char.IsUpper(piece.Value) == byWhite)
                {
                    var upper = char.ToUpperInvariant(piece.Value);
                    if (upper == expectedPiece || upper == 'Q')
                    {
                        return true;
                    }
                }

                break;
            }
        }

        return false;
    }

    private static string FormatSan(
        PositionState position,
        GeneratedMove move,
        IReadOnlyList<GeneratedMove> allLegalMoves,
        CancellationToken cancellationToken)
    {
        string san;
        if (move.Flags.HasFlag(MoveFlags.CastleKingSide))
        {
            san = "O-O";
        }
        else if (move.Flags.HasFlag(MoveFlags.CastleQueenSide))
        {
            san = "O-O-O";
        }
        else
        {
            var builder = new StringBuilder(8);
            var upperPiece = char.ToUpperInvariant(move.Piece);
            var pawn = upperPiece == 'P';
            if (!pawn)
            {
                builder.Append(upperPiece);
                AppendDisambiguation(builder, move, allLegalMoves);
            }
            else if (move.Flags.HasFlag(MoveFlags.Capture))
            {
                builder.Append((char)('a' + move.From.File));
            }

            if (move.Flags.HasFlag(MoveFlags.Capture))
            {
                builder.Append('x');
            }

            builder.Append(move.To.Name);
            if (move.Promotion is { } promotion)
            {
                builder.Append('=').Append(char.ToUpperInvariant(promotion));
            }

            san = builder.ToString();
        }

        var next = ApplyUnchecked(position, move);
        if (IsKingInCheck(next, next.WhiteToMove))
        {
            cancellationToken.ThrowIfCancellationRequested();
            san += GenerateLegalMoves(next, cancellationToken).Count == 0 ? "#" : "+";
        }

        return san;
    }

    private static void AppendDisambiguation(
        StringBuilder builder,
        GeneratedMove move,
        IReadOnlyList<GeneratedMove> allLegalMoves)
    {
        var alternatives = allLegalMoves.Where(candidate =>
                candidate.From != move.From &&
                candidate.To == move.To &&
                char.ToUpperInvariant(candidate.Piece) == char.ToUpperInvariant(move.Piece))
            .ToArray();
        if (alternatives.Length == 0)
        {
            return;
        }

        var fileUnique = alternatives.All(candidate => candidate.From.File != move.From.File);
        var rankUnique = alternatives.All(candidate => candidate.From.Rank != move.From.Rank);
        if (fileUnique)
        {
            builder.Append((char)('a' + move.From.File));
        }
        else if (rankUnique)
        {
            builder.Append(move.From.Rank + 1);
        }
        else
        {
            builder.Append(move.From.Name);
        }
    }

    private static bool SanEquivalent(string left, string right)
    {
        if (string.Equals(left, right, StringComparison.Ordinal))
        {
            return true;
        }

        return string.Equals(StripCheckSuffix(left), StripCheckSuffix(right), StringComparison.Ordinal);
    }

    private static string NormalizeSan(string san)
    {
        var value = MoveNumberPrefixRegex().Replace((san ?? string.Empty).Trim(), string.Empty);
        value = value.Replace('0', 'O')
            .Replace("–", "-", StringComparison.Ordinal)
            .Replace("×", "x", StringComparison.Ordinal)
            .Replace("e.p.", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("ep", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Trim();
        value = AnnotationSuffixRegex().Replace(value, string.Empty);
        value = value.Replace("++", "+", StringComparison.Ordinal);
        return value;
    }

    private static string StripCheckSuffix(string san) => san.TrimEnd('+', '#');

    private static string NormalizeUci(string uci)
    {
        var value = (uci ?? string.Empty).Trim().ToLowerInvariant();
        if (!UciRegex().IsMatch(value))
        {
            throw new FormatException("UCI must be four square characters plus an optional promotion piece.");
        }

        return value;
    }

    private static bool IsInside(int file, int rank) => file is >= 0 and < 8 && rank is >= 0 and < 8;

    private static readonly (int File, int Rank)[] KnightOffsets =
    [
        (-2, -1), (-2, 1), (-1, -2), (-1, 2),
        (1, -2), (1, 2), (2, -1), (2, 1),
    ];

    private static readonly (int File, int Rank)[] KingOffsets =
    [
        (-1, -1), (-1, 0), (-1, 1), (0, -1),
        (0, 1), (1, -1), (1, 0), (1, 1),
    ];

    private static readonly (int File, int Rank)[] BishopDirections =
    [
        (-1, -1), (-1, 1), (1, -1), (1, 1),
    ];

    private static readonly (int File, int Rank)[] RookDirections =
    [
        (-1, 0), (1, 0), (0, -1), (0, 1),
    ];

    private static readonly (int File, int Rank)[] QueenDirections = [.. BishopDirections, .. RookDirections];

    [Flags]
    private enum MoveFlags
    {
        None = 0,
        Capture = 1,
        PawnDouble = 2,
        EnPassant = 4,
        CastleKingSide = 8,
        CastleQueenSide = 16,
        Promotion = 32,
    }

    private readonly record struct GeneratedMove(
        Square From,
        Square To,
        char Piece,
        char? Captured,
        char? Promotion,
        MoveFlags Flags)
    {
        public string Uci => From.Name + To.Name + (Promotion is { } promotion ? char.ToLowerInvariant(promotion) : string.Empty);
    }

    private sealed class PositionState
    {
        private static readonly HashSet<char> ValidPieces = [.. "KQRBNPkqrbnp"];

        private PositionState(
            char?[] board,
            bool whiteToMove,
            string castlingRights,
            Square? enPassant,
            int halfmoveClock,
            int fullmoveNumber)
        {
            Board = board;
            WhiteToMove = whiteToMove;
            CastlingRights = castlingRights;
            EnPassant = enPassant;
            HalfmoveClock = halfmoveClock;
            FullmoveNumber = fullmoveNumber;
        }

        public char?[] Board { get; }
        public bool WhiteToMove { get; set; }
        public string CastlingRights { get; set; }
        public Square? EnPassant { get; set; }
        public int HalfmoveClock { get; set; }
        public int FullmoveNumber { get; set; }
        public char? this[Square square] => Board[square.Index];
        public string PositionKey => $"{PiecePlacement()} {(WhiteToMove ? "w" : "b")} {(string.IsNullOrEmpty(CastlingRights) ? "-" : CastlingRights)} {RepetitionEnPassant()}";

        public static PositionState Parse(string fen)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(fen);
            var sections = fen.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (sections.Length is < 4 or > 6)
            {
                throw new FormatException("FEN must contain four to six fields.");
            }

            var ranks = sections[0].Split('/');
            if (ranks.Length != 8)
            {
                throw new FormatException("FEN piece placement must contain exactly eight ranks.");
            }

            var board = new char?[64];
            for (var fenRank = 0; fenRank < 8; fenRank++)
            {
                var file = 0;
                foreach (var symbol in ranks[fenRank])
                {
                    if (symbol is >= '1' and <= '8')
                    {
                        file += symbol - '0';
                    }
                    else
                    {
                        if (!ValidPieces.Contains(symbol) || file >= 8)
                        {
                            throw new FormatException($"Invalid FEN piece placement at rank {8 - fenRank}.");
                        }

                        board[new Square(file, 7 - fenRank).Index] = symbol;
                        file++;
                    }
                }

                if (file != 8)
                {
                    throw new FormatException($"FEN rank {8 - fenRank} does not expand to eight squares.");
                }
            }

            var whiteToMove = sections[1] switch
            {
                "w" => true,
                "b" => false,
                _ => throw new FormatException("FEN side-to-move field must be 'w' or 'b'."),
            };
            var castling = sections[2] == "-" ? string.Empty : sections[2];
            if (castling.Any(character => !"KQkq".Contains(character)) ||
                castling.Distinct().Count() != castling.Length)
            {
                throw new FormatException("Invalid FEN castling rights.");
            }

            Square? enPassant = null;
            if (sections[3] != "-")
            {
                if (!Square.TryParse(sections[3], out var parsed) || parsed.Rank is not (2 or 5))
                {
                    throw new FormatException("Invalid FEN en-passant square.");
                }

                enPassant = parsed;
            }

            var halfmove = sections.Length > 4 && int.TryParse(sections[4], out var parsedHalfmove) && parsedHalfmove >= 0
                ? parsedHalfmove
                : 0;
            var fullmove = sections.Length > 5 && int.TryParse(sections[5], out var parsedFullmove) && parsedFullmove >= 1
                ? parsedFullmove
                : 1;
            return new PositionState(board, whiteToMove, string.Concat("KQkq".Where(castling.Contains)), enPassant, halfmove, fullmove);
        }

        public PositionState Clone() => new(
            (char?[])Board.Clone(),
            WhiteToMove,
            CastlingRights,
            EnPassant,
            HalfmoveClock,
            FullmoveNumber);

        public string ToFen() => $"{PiecePlacement()} {(WhiteToMove ? "w" : "b")} {(string.IsNullOrEmpty(CastlingRights) ? "-" : CastlingRights)} {RepetitionEnPassant()} {HalfmoveClock} {FullmoveNumber}";

        private string RepetitionEnPassant()
        {
            if (EnPassant is not { } target)
            {
                return "-";
            }

            var pawnRank = target.Rank + (WhiteToMove ? -1 : 1);
            var pawn = WhiteToMove ? 'P' : 'p';
            var capturedPawn = WhiteToMove ? 'p' : 'P';
            if (!IsInside(target.File, pawnRank) || this[new Square(target.File, pawnRank)] != capturedPawn)
            {
                return "-";
            }

            foreach (var file in new[] { target.File - 1, target.File + 1 })
            {
                if (!IsInside(file, pawnRank))
                {
                    continue;
                }

                var source = new Square(file, pawnRank);
                if (this[source] != pawn)
                {
                    continue;
                }

                var move = new GeneratedMove(
                    source,
                    target,
                    pawn,
                    capturedPawn,
                    null,
                    MoveFlags.Capture | MoveFlags.EnPassant);
                if (!IsKingInCheck(ApplyUnchecked(this, move), WhiteToMove))
                {
                    return target.Name;
                }
            }

            return "-";
        }

        private string PiecePlacement()
        {
            var builder = new StringBuilder(72);
            for (var rank = 7; rank >= 0; rank--)
            {
                var empty = 0;
                for (var file = 0; file < 8; file++)
                {
                    var piece = Board[new Square(file, rank).Index];
                    if (piece is null)
                    {
                        empty++;
                        continue;
                    }

                    if (empty > 0)
                    {
                        builder.Append(empty);
                        empty = 0;
                    }

                    builder.Append(piece.Value);
                }

                if (empty > 0)
                {
                    builder.Append(empty);
                }

                if (rank > 0)
                {
                    builder.Append('/');
                }
            }

            return builder.ToString();
        }
    }

    [GeneratedRegex("^[a-h][1-8][a-h][1-8][qrbn]?$", RegexOptions.CultureInvariant)]
    private static partial Regex UciRegex();

    [GeneratedRegex("^(?:[0-9]+\\.(?:\\.\\.)?\\s*)", RegexOptions.CultureInvariant)]
    private static partial Regex MoveNumberPrefixRegex();

    [GeneratedRegex("[!?]+$", RegexOptions.CultureInvariant)]
    private static partial Regex AnnotationSuffixRegex();

    [GeneratedRegex("([a-h][1-8])(?:=[QRBN])?$", RegexOptions.CultureInvariant)]
    private static partial Regex SanTargetRegex();
}

public sealed record MoveResolution(LegalMove Move, string Fen, string PositionKey);
