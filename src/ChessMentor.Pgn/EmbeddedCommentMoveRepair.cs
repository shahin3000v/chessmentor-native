using System.Text;
using System.Text.RegularExpressions;
using ChessMentor.Chess;

namespace ChessMentor.Pgn;

/// <summary>
/// Converts legal, explicitly numbered move sequences embedded in prose comments
/// into real PGN variations. A sequence must contain at least two legal plies, so
/// ordinary square references remain prose. This mirrors the current Python
/// application's embedded-move repair without making python-chess authoritative.
/// </summary>
public sealed partial class EmbeddedCommentMoveRepair(ManagedChessRules? rules = null)
{
    private static readonly IReadOnlyDictionary<string, int> AnnotationNags =
        new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["!"] = 1,
            ["?"] = 2,
            ["!!"] = 3,
            ["??"] = 4,
            ["!?"] = 5,
            ["?!"] = 6,
        };
    private readonly ManagedChessRules _rules = rules ?? ManagedChessRules.Instance;

    public EmbeddedCommentMoveRepairStats Repair(
        PgnDocument document,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        var commentsChecked = 0;
        var sequencesRepaired = 0;
        var movesRepaired = 0;
        foreach (var game in document.Games)
        {
            var nodes = game.Root.Descendants().Prepend(game.Root).ToArray();
            foreach (var anchor in nodes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var comment = PgnTreeEditor.CommentText(anchor);
                if (string.IsNullOrWhiteSpace(comment) || string.IsNullOrWhiteSpace(anchor.Fen))
                {
                    continue;
                }

                commentsChecked++;
                var sequences = FindRepairableSequences(comment, anchor.Fen!, cancellationToken);
                if (sequences.Count == 0)
                {
                    continue;
                }

                var added = ApplySequences(game, anchor, comment, sequences, cancellationToken);
                sequencesRepaired += sequences.Count;
                movesRepaired += added;
            }
        }

        return new EmbeddedCommentMoveRepairStats(commentsChecked, sequencesRepaired, movesRepaired);
    }

    internal IReadOnlyList<RepairedSequence> FindRepairableSequences(
        string text,
        string anchorFen,
        CancellationToken cancellationToken = default)
    {
        var tokens = Tokenize(text);
        var sequences = new List<RepairedSequence>();
        var index = 0;
        while (index < tokens.Count)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (tokens[index].Kind != CandidateKind.Number || !NumberMatches(anchorFen, tokens[index]))
            {
                index++;
                continue;
            }

            var (sequence, nextIndex) = BuildSequence(
                text,
                tokens,
                index,
                anchorFen,
                cancellationToken);
            if (sequence is { Moves.Count: >= 2 })
            {
                sequences.Add(sequence);
            }

            index = Math.Max(nextIndex, index + 1);
        }

        for (var position = 0; position < sequences.Count; position++)
        {
            var sequence = sequences[position];
            var boundary = position + 1 < sequences.Count ? sequences[position + 1].Start : text.Length;
            if (sequence.End < boundary)
            {
                sequence.TrailingText += text[sequence.End..boundary];
                sequence.End = boundary;
            }
        }

        return sequences;
    }

    private (RepairedSequence? Sequence, int NextIndex) BuildSequence(
        string text,
        IReadOnlyList<CandidateToken> tokens,
        int startIndex,
        string anchorFen,
        CancellationToken cancellationToken)
    {
        var numberToken = tokens[startIndex];
        if (numberToken.Kind != CandidateKind.Number || !NumberMatches(anchorFen, numberToken))
        {
            return (null, startIndex + 1);
        }

        var fen = anchorFen;
        var sequence = new RepairedSequence(numberToken.Start, numberToken.End);
        CandidateToken? pendingNumber = numberToken;
        var previousMoveEnd = numberToken.End;
        var index = startIndex + 1;
        while (index < tokens.Count)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var token = tokens[index];
            if (token.Kind == CandidateKind.Number)
            {
                if (sequence.Moves.Count == 0 && pendingNumber != numberToken)
                {
                    return (null, index);
                }

                if (NumberMatches(fen, token))
                {
                    pendingNumber = token;
                    index++;
                    continue;
                }

                if (sequence.Moves.Count > 0 && NumberMatches(anchorFen, token))
                {
                    sequence.End = token.Start;
                    sequence.TrailingText = text[previousMoveEnd..token.Start];
                    return (sequence, index);
                }

                if (sequence.Moves.Count > 0)
                {
                    sequence.End = previousMoveEnd;
                    return (sequence, index);
                }

                return (null, index + 1);
            }

            if (sequence.Moves.Count == 0)
            {
                var gap = pendingNumber is null ? string.Empty : text[pendingNumber.End..token.Start];
                if (!OnlySpacing(gap))
                {
                    return (null, index);
                }
            }
            else if (pendingNumber is not null)
            {
                var gap = text[pendingNumber.End..token.Start];
                if (!OnlySpacing(gap))
                {
                    sequence.End = previousMoveEnd;
                    return (sequence, index);
                }
            }
            else
            {
                var gap = text[previousMoveEnd..token.Start];
                if (!OnlySpacing(gap))
                {
                    index++;
                    continue;
                }
            }

            var san = SanFromToken(token);
            if (!_rules.TryResolveSan(fen, san, out var resolution, cancellationToken))
            {
                if (pendingNumber is not null)
                {
                    if (sequence.Moves.Count > 0)
                    {
                        sequence.End = previousMoveEnd;
                        return (sequence, index);
                    }

                    return (null, index + 1);
                }

                index++;
                continue;
            }

            var gapBefore = sequence.Moves.Count == 0
                ? string.Empty
                : StripMoveNumbers(text[previousMoveEnd..token.Start]);
            sequence.Moves.Add(new RepairedMove(
                resolution!.Move,
                resolution.Fen,
                token.Annotation,
                gapBefore,
                pendingNumber?.Black == true));
            fen = resolution.Fen;
            previousMoveEnd = token.End;
            sequence.End = token.End;
            pendingNumber = null;
            index++;
        }

        if (sequence.Moves.Count == 0)
        {
            return (null, index);
        }

        sequence.End = text.Length;
        sequence.TrailingText = text[previousMoveEnd..];
        return (sequence, index);
    }

    private static int ApplySequences(
        PgnGame game,
        PgnMoveNode anchor,
        string originalComment,
        IReadOnlyList<RepairedSequence> sequences,
        CancellationToken cancellationToken)
    {
        PgnTreeEditor.SetComment(anchor, originalComment[..sequences[0].Start].Trim());
        var movesAdded = 0;
        foreach (var sequence in sequences)
        {
            var parent = anchor;
            foreach (var repaired in sequence.Moves)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var result = PgnTreeEditor.AddMove(game, parent, repaired.Move, repaired.ResultingFen);
                var child = result.Node;
                child.ForceMoveNumber |= repaired.ForceMoveNumber;
                if (!string.IsNullOrWhiteSpace(repaired.Annotation))
                {
                    child.AddAnnotation(repaired.Annotation);
                    if (AnnotationNags.TryGetValue(repaired.Annotation, out var nag))
                    {
                        child.AddNag(nag);
                    }
                }

                if (!string.IsNullOrWhiteSpace(repaired.GapBefore))
                {
                    AppendComment(parent, repaired.GapBefore);
                }

                parent = child;
                movesAdded++;
            }

            AppendComment(parent, sequence.TrailingText);
        }

        return movesAdded;
    }

    private static void AppendComment(PgnMoveNode node, string value)
    {
        var clean = CollapseWhitespace(value);
        if (clean.Length == 0)
        {
            return;
        }

        var existing = CollapseWhitespace(PgnTreeEditor.CommentText(node));
        if (existing.Length == 0)
        {
            PgnTreeEditor.SetComment(node, clean);
        }
        else if (!existing.Contains(clean, StringComparison.Ordinal))
        {
            PgnTreeEditor.SetComment(node, $"{existing} {clean}");
        }
    }

    private static IReadOnlyList<CandidateToken> Tokenize(string text)
    {
        var normalized = NormalizeSameLength(text);
        var tokens = new List<CandidateToken>();
        foreach (Match match in MoveNumberRegex().Matches(normalized))
        {
            tokens.Add(new CandidateToken(
                CandidateKind.Number,
                match.Index,
                match.Index + match.Length,
                text.Substring(match.Index, match.Length),
                int.Parse(match.Groups["number"].Value, System.Globalization.CultureInfo.InvariantCulture),
                string.Equals(match.Groups["dots"].Value, "...", StringComparison.Ordinal),
                string.Empty));
        }

        foreach (Match match in BareBlackPrefixRegex().Matches(normalized))
        {
            tokens.Add(new CandidateToken(
                CandidateKind.Number,
                match.Index,
                match.Index + match.Length,
                text.Substring(match.Index, match.Length),
                null,
                true,
                string.Empty));
        }

        foreach (Match match in SanRegex().Matches(normalized))
        {
            tokens.Add(new CandidateToken(
                CandidateKind.San,
                match.Index,
                match.Index + match.Length,
                text.Substring(match.Index, match.Length),
                null,
                false,
                match.Groups["annotation"].Value));
        }

        var filtered = new List<CandidateToken>();
        var occupiedUntil = -1;
        foreach (var token in tokens.OrderBy(static token => token.Start)
                     .ThenBy(static token => token.Kind == CandidateKind.Number ? 0 : 1)
                     .ThenBy(static token => token.End))
        {
            if (token.Start < occupiedUntil)
            {
                continue;
            }

            filtered.Add(token);
            occupiedUntil = token.End;
        }

        return filtered;
    }

    private static bool NumberMatches(string fen, CandidateToken token)
    {
        if (token.Kind != CandidateKind.Number)
        {
            return false;
        }

        var fields = fen.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var blackToMove = fields.Length > 1 && string.Equals(fields[1], "b", StringComparison.Ordinal);
        if (token.Number is null)
        {
            return token.Black && blackToMove;
        }

        var fullmove = fields.Length > 5 && int.TryParse(
            fields[5],
            System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture,
            out var value)
            ? value
            : 1;
        return token.Number == fullmove && token.Black == blackToMove;
    }

    private static string SanFromToken(CandidateToken token)
    {
        var value = NormalizeSameLength(token.Raw);
        if (token.Annotation.Length > 0 && value.EndsWith(token.Annotation, StringComparison.Ordinal))
        {
            value = value[..^token.Annotation.Length];
        }

        value = value.Replace("0-0-0", "O-O-O", StringComparison.Ordinal)
            .Replace("0-0", "O-O", StringComparison.Ordinal);
        if (value.StartsWith('P'))
        {
            value = value[1..];
        }

        return value.Replace("=P", string.Empty, StringComparison.Ordinal);
    }

    private static string StripMoveNumbers(string value)
    {
        var normalized = NormalizeSameLength(value);
        var spans = MoveNumberRegex().Matches(normalized).Select(static match => (match.Index, match.Length))
            .Concat(BareBlackPrefixRegex().Matches(normalized).Select(static match => (match.Index, match.Length)))
            .OrderBy(static span => span.Index)
            .ToArray();
        if (spans.Length == 0)
        {
            return value;
        }

        var output = new StringBuilder(value.Length);
        var cursor = 0;
        foreach (var span in spans)
        {
            output.Append(value, cursor, span.Index - cursor);
            cursor = span.Index + span.Length;
        }

        output.Append(value, cursor, value.Length - cursor);
        return output.ToString();
    }

    private static string NormalizeSameLength(string value) => string.Create(
        value.Length,
        value,
        static (target, source) =>
        {
            const string PersianDigits = "۰۱۲۳۴۵۶۷۸۹";
            const string ArabicDigits = "٠١٢٣٤٥٦٧٨٩";
            for (var index = 0; index < source.Length; index++)
            {
                var character = source[index];
                var persianIndex = PersianDigits.IndexOf(character);
                var arabicIndex = ArabicDigits.IndexOf(character);
                target[index] = persianIndex >= 0
                    ? (char)('0' + persianIndex)
                    : arabicIndex >= 0
                        ? (char)('0' + arabicIndex)
                        : character switch
                        {
                            '♔' or '♚' => 'K',
                            '♕' or '♛' => 'Q',
                            '♖' or '♜' => 'R',
                            '♗' or '♝' => 'B',
                            '♘' or '♞' or '\ue028' or '\uf028' => 'N',
                            '♙' or '♟' => 'P',
                            _ => character,
                        };
            }
        });

    private static bool OnlySpacing(string value) => value.Length == 0 || value.All(char.IsWhiteSpace);

    private static string CollapseWhitespace(string value) =>
        WhitespaceRegex().Replace(value.Replace("\0", " ", StringComparison.Ordinal), " ").Trim();

    [GeneratedRegex(@"(?<!\d)(?<number>\d{1,3})\s*(?<dots>\.\.\.|\.)(?!\.)", RegexOptions.CultureInvariant)]
    private static partial Regex MoveNumberRegex();

    [GeneratedRegex(@"(?<![.\d])(?<dots>\.\.\.)(?!\.)(?=\s*(?:O-O-O|O-O|0-0-0|0-0|[KQRBNP]?[a-h]?[1-8]?(?:x|-)?[a-h][1-8](?:=[QRBNP])?[+#]?)(?:!!|\?\?|!\?|\?!|!|\?)?(?![A-Za-z0-9]))", RegexOptions.CultureInvariant)]
    private static partial Regex BareBlackPrefixRegex();

    [GeneratedRegex(@"(?<![A-Za-z0-9])(?<san>(?:O-O-O|O-O|0-0-0|0-0|[KQRBNP]?[a-h]?[1-8]?(?:x|-)?[a-h][1-8](?:=[QRBNP])?[+#]?)(?<annotation>!!|\?\?|!\?|\?!|!|\?)?)(?![A-Za-z0-9])", RegexOptions.CultureInvariant)]
    private static partial Regex SanRegex();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespaceRegex();

    private enum CandidateKind
    {
        Number,
        San,
    }

    private sealed record CandidateToken(
        CandidateKind Kind,
        int Start,
        int End,
        string Raw,
        int? Number,
        bool Black,
        string Annotation);

    internal sealed class RepairedSequence(int start, int end)
    {
        public int Start { get; } = start;
        public int End { get; set; } = end;
        public List<RepairedMove> Moves { get; } = [];
        public string TrailingText { get; set; } = string.Empty;
    }

    internal sealed record RepairedMove(
        LegalMove Move,
        string ResultingFen,
        string Annotation,
        string GapBefore,
        bool ForceMoveNumber);
}

public sealed record EmbeddedCommentMoveRepairStats(
    int CommentsChecked,
    int SequencesRepaired,
    int MovesRepaired);
