using ChessMentor.Chess;
using ChessMentor.Core;

namespace ChessMentor.Pgn;

/// <summary>
/// Resolves lossless syntax nodes to legal UCI moves and resulting positions.
/// Parsing and semantic resolution stay separate so unsupported SAN never damages
/// the original token stream or prevents the remainder of a document from opening.
/// </summary>
public sealed class PgnSemanticEnricher(ManagedChessRules? rules = null)
{
    private readonly ManagedChessRules _rules = rules ?? ManagedChessRules.Instance;

    public Task<PgnSemanticResult> EnrichAsync(
        PgnDocument document,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        return Task.Run(() => Enrich(document, cancellationToken), cancellationToken);
    }

    public PgnSemanticResult Enrich(
        PgnDocument document,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        var diagnostics = new List<PgnSemanticDiagnostic>();
        var resolved = 0;
        var unresolved = 0;

        foreach (var game in document.Games)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var initialFen = string.IsNullOrWhiteSpace(game.Header("FEN"))
                ? FenPosition.Initial
                : game.Header("FEN")!;
            string rootPositionKey;
            try
            {
                rootPositionKey = ManagedChessRules.PositionKey(initialFen);
            }
            catch (FormatException exception)
            {
                diagnostics.Add(new PgnSemanticDiagnostic(
                    game.Id,
                    game.Root.StableId,
                    string.Empty,
                    $"Invalid starting FEN: {exception.Message}"));
                unresolved += game.NodeCount;
                continue;
            }

            game.Root.Fen = initialFen;
            game.Root.PositionKey = rootPositionKey;
            game.Root.TranspositionGroupId = StableId.Create("position", rootPositionKey);

            var pending = new Stack<(PgnMoveNode Node, string ParentFen)>();
            for (var index = game.Root.Children.Count - 1; index >= 0; index--)
            {
                pending.Push((game.Root.Children[index], initialFen));
            }

            while (pending.TryPop(out var item))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var fenFields = item.ParentFen.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                item.Node.IsWhiteMove = fenFields.Length < 2 || fenFields[1] == "w";
                item.Node.FullmoveNumber = fenFields.Length > 5 && int.TryParse(fenFields[5], out var fullmove)
                    ? fullmove
                    : Math.Max(1, (item.Node.Ply + 1) / 2);
                if (!_rules.TryResolveSan(
                        item.ParentFen,
                        item.Node.RawSan,
                        out var resolution,
                        cancellationToken))
                {
                    unresolved++;
                    diagnostics.Add(new PgnSemanticDiagnostic(
                        game.Id,
                        item.Node.StableId,
                        item.Node.RawSan,
                        "The move could not be matched to a legal move in its parent position."));
                    unresolved += item.Node.Descendants().Count();
                    continue;
                }

                item.Node.Uci = resolution!.Move.Uci;
                item.Node.Fen = resolution.Fen;
                item.Node.PositionKey = resolution.PositionKey;
                item.Node.TranspositionGroupId = StableId.Create("position", resolution.PositionKey);
                resolved++;

                for (var index = item.Node.Children.Count - 1; index >= 0; index--)
                {
                    pending.Push((item.Node.Children[index], resolution.Fen));
                }
            }
        }

        return new PgnSemanticResult(resolved, unresolved, diagnostics);
    }
}

public sealed record PgnSemanticDiagnostic(string GameId, string NodeId, string San, string Message);

public sealed record PgnSemanticResult(
    int ResolvedNodeCount,
    int UnresolvedNodeCount,
    IReadOnlyList<PgnSemanticDiagnostic> Diagnostics)
{
    public bool IsComplete => UnresolvedNodeCount == 0;
}
