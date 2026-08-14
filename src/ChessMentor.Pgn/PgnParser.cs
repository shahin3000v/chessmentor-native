using System.Text.RegularExpressions;
using ChessMentor.Core;

namespace ChessMentor.Pgn;

public sealed partial class PgnParser
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

    public Task<PgnDocument> ParseAsync(string source, CancellationToken cancellationToken = default) =>
        Task.Run(() => Parse(source, cancellationToken), cancellationToken);

    public PgnDocument Parse(string source, CancellationToken cancellationToken = default)
    {
        source ??= string.Empty;
        var tokenizer = new PgnTokenizer();
        var (tokens, lexicalDiagnostics) = tokenizer.Tokenize(source, cancellationToken);
        var diagnostics = lexicalDiagnostics.ToList();
        var games = new List<PgnGame>();
        ParseState? state = null;

        foreach (var token in tokens)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (token.Kind == PgnTokenKind.Whitespace)
            {
                continue;
            }

            if (token.Kind == PgnTokenKind.Header)
            {
                var parsedHeader = ParseHeader(token, diagnostics);
                var startsNew = state is null || state.Game.MovetextStarted ||
                    (parsedHeader is { Name: "Event" } && state.Game.Headers.Any(static header => header.Name == "Event"));
                if (startsNew)
                {
                    DiagnoseUnclosedVariations(state, diagnostics, token);
                    state = StartGame(games);
                }

                if (parsedHeader is not null)
                {
                    state!.Game.AddHeader(parsedHeader);
                }

                continue;
            }

            if (state is null || (state.Game.Result != "*" && token.Kind is PgnTokenKind.Symbol or PgnTokenKind.MoveNumber))
            {
                DiagnoseUnclosedVariations(state, diagnostics, token);
                state = StartGame(games);
            }

            switch (token.Kind)
            {
                case PgnTokenKind.MoveNumber:
                    state.PendingMoveNumber = token;
                    state.ExpectingMove = true;
                    state.Game.MovetextStarted = true;
                    break;
                case PgnTokenKind.BraceComment:
                case PgnTokenKind.LineComment:
                    AttachComment(state, new PgnComment(token));
                    break;
                case PgnTokenKind.VariationStart:
                    state.Variations.Push(state.Current);
                    state.Current = state.Current.Parent ?? state.Current;
                    state.PendingMoveNumber = null;
                    state.PendingStartingComments.Clear();
                    state.ExpectingMove = true;
                    state.Game.MovetextStarted = true;
                    break;
                case PgnTokenKind.VariationEnd:
                    if (state.Variations.TryPop(out var returnNode))
                    {
                        state.Current = returnNode;
                    }
                    else
                    {
                        diagnostics.Add(new PgnDiagnostic("PGN003", "Unmatched variation close.", token.Offset, token.Line, token.Column));
                    }

                    state.PendingMoveNumber = null;
                    state.PendingStartingComments.Clear();
                    state.ExpectingMove = false;
                    break;
                case PgnTokenKind.Nag:
                    if (state.Current != state.Game.Root && int.TryParse(token.RawText.AsSpan(1), out var nag))
                    {
                        state.Current.AddNag(nag);
                    }
                    break;
                case PgnTokenKind.Annotation:
                    AddAnnotation(state.Current, token.RawText);
                    break;
                case PgnTokenKind.Result:
                    state.Game.Result = token.RawText;
                    state.Game.MovetextStarted = true;
                    break;
                case PgnTokenKind.Symbol:
                    AddMove(state, token);
                    break;
            }
        }

        foreach (var game in games)
        {
            game.EnsureIdentity();
        }

        if (state is not null && state.Variations.Count > 0)
        {
            diagnostics.Add(new PgnDiagnostic("PGN004", "One or more variations are not closed.", source.Length, 0, 0));
        }

        return new PgnDocument(source, tokens, games, diagnostics);
    }

    private static ParseState StartGame(List<PgnGame> games)
    {
        var game = new PgnGame(games.Count);
        games.Add(game);
        return new ParseState(game);
    }

    private static void DiagnoseUnclosedVariations(
        ParseState? state,
        List<PgnDiagnostic> diagnostics,
        PgnToken nextToken)
    {
        if (state is not null && state.Variations.Count > 0)
        {
            diagnostics.Add(new PgnDiagnostic(
                "PGN004",
                "One or more variations are not closed before the next game.",
                nextToken.Offset,
                nextToken.Line,
                nextToken.Column));
        }
    }

    private static void AttachComment(ParseState state, PgnComment comment)
    {
        if (state.ExpectingMove)
        {
            state.PendingStartingComments.Add(comment);
        }
        else
        {
            state.Current.AddComment(comment);
        }
    }

    private static void AddMove(ParseState state, PgnToken token)
    {
        state.Game.EnsureIdentity();
        var (san, inlineAnnotation) = SplitAnnotation(token.RawText);
        var occurrence = state.Current.Children.Count(child =>
            string.Equals(child.RawSan, san, StringComparison.Ordinal));
        var stableId = StableId.Create("node", state.Game.Id, state.Current.StableId, NormalizeSan(san), occurrence);
        var node = new PgnMoveNode(state.Current, stableId, san, state.Current.Ply + 1, token)
        {
            MoveNumberToken = state.PendingMoveNumber,
            ForceMoveNumber = state.PendingMoveNumber?.RawText.Contains("...", StringComparison.Ordinal) == true,
        };

        foreach (var comment in state.PendingStartingComments)
        {
            node.AddStartingComment(comment);
        }

        state.PendingStartingComments.Clear();
        state.Current.AddChild(node);
        state.Current = node;
        state.PendingMoveNumber = null;
        state.ExpectingMove = false;
        state.Game.MovetextStarted = true;
        if (!string.IsNullOrEmpty(inlineAnnotation))
        {
            AddAnnotation(node, inlineAnnotation);
        }
    }

    private static void AddAnnotation(PgnMoveNode node, string annotation)
    {
        if (node.IsRoot || !AnnotationNags.TryGetValue(annotation, out var nag))
        {
            return;
        }

        node.AddAnnotation(annotation);
        node.AddNag(nag);
    }

    private static (string San, string Annotation) SplitAnnotation(string raw)
    {
        foreach (var suffix in new[] { "!!", "??", "!?", "?!", "!", "?" })
        {
            if (raw.Length > suffix.Length && raw.EndsWith(suffix, StringComparison.Ordinal))
            {
                return (raw[..^suffix.Length], suffix);
            }
        }

        return (raw, string.Empty);
    }

    private static string NormalizeSan(string san) =>
        san.Replace("0-0-0", "O-O-O", StringComparison.Ordinal)
            .Replace("0-0", "O-O", StringComparison.Ordinal)
            .Trim();

    private static PgnHeader? ParseHeader(PgnToken token, List<PgnDiagnostic> diagnostics)
    {
        var match = HeaderRegex().Match(token.RawText);
        if (!match.Success)
        {
            diagnostics.Add(new PgnDiagnostic("PGN005", "Invalid PGN tag pair.", token.Offset, token.Line, token.Column));
            return null;
        }

        var value = match.Groups[2].Value
            .Replace("\\\"", "\"", StringComparison.Ordinal)
            .Replace("\\\\", "\\", StringComparison.Ordinal);
        return new PgnHeader(match.Groups[1].Value, value, token);
    }

    [GeneratedRegex("^\\[\\s*([A-Za-z0-9_]+)\\s+\"((?:\\\\.|[^\"])*)\"\\s*\\]$", RegexOptions.CultureInvariant)]
    private static partial Regex HeaderRegex();

    private sealed class ParseState(PgnGame game)
    {
        public PgnGame Game { get; } = game;
        public PgnMoveNode Current { get; set; } = game.Root;
        public Stack<PgnMoveNode> Variations { get; } = [];
        public List<PgnComment> PendingStartingComments { get; } = [];
        public PgnToken? PendingMoveNumber { get; set; }
        public bool ExpectingMove { get; set; }
    }
}
