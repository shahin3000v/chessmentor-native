using System.Diagnostics;
using System.Text;
using ChessMentor.Pgn;

namespace ChessMentor.Viewer;

public sealed class ViewerDocumentLoader(
    PgnParser? parser = null,
    PgnSemanticEnricher? semanticEnricher = null)
{
    private readonly PgnParser _parser = parser ?? new PgnParser();
    private readonly PgnSemanticEnricher _semanticEnricher = semanticEnricher ?? new PgnSemanticEnricher();
    private readonly EmbeddedCommentMoveRepair _embeddedCommentRepair = new();

    public async Task<LoadedPgnBatch> LoadTextAsync(
        string text,
        string sourceName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceName);
        if (string.IsNullOrWhiteSpace(text))
        {
            return new LoadedPgnBatch(
                Array.Empty<LoadedPgnSource>(),
                0,
                0,
                0,
                0,
                [$"{sourceName}: file is empty."]);
        }

        var parseWatch = Stopwatch.StartNew();
        var document = await _parser.ParseAsync(text, cancellationToken).ConfigureAwait(false);
        parseWatch.Stop();
        if (document.Games.Count == 0)
        {
            return new LoadedPgnBatch(
                Array.Empty<LoadedPgnSource>(),
                0,
                0,
                parseWatch.Elapsed.TotalMilliseconds,
                0,
                [$"{sourceName}: no PGN game was found."]);
        }

        var semanticWatch = Stopwatch.StartNew();
        var semantic = await _semanticEnricher.EnrichAsync(document, cancellationToken).ConfigureAwait(false);
        var repair = await Task.Run(
            () => _embeddedCommentRepair.Repair(document, cancellationToken),
            cancellationToken).ConfigureAwait(false);
        if (repair.MovesRepaired > 0)
        {
            semantic = await _semanticEnricher.EnrichAsync(document, cancellationToken).ConfigureAwait(false);
        }
        semanticWatch.Stop();
        var diagnostics = document.Diagnostics.Select(diagnostic =>
                $"{sourceName}:{diagnostic.Line}:{diagnostic.Column} {diagnostic.Message}")
            .Concat(semantic.Diagnostics.Select(diagnostic =>
                $"{sourceName} [{diagnostic.San}] {diagnostic.Message}"))
            .ToArray();
        var source = new LoadedPgnSource(sourceName, sourceName, document, semantic, repair);
        return new LoadedPgnBatch(
            [source],
            document.Games.Count,
            document.NodeCount,
            parseWatch.Elapsed.TotalMilliseconds,
            semanticWatch.Elapsed.TotalMilliseconds,
            diagnostics);
    }

    public async Task<LoadedPgnBatch> LoadAsync(
        IReadOnlyList<string> filePaths,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filePaths);
        if (filePaths.Count == 0)
        {
            return new LoadedPgnBatch(
                Array.Empty<LoadedPgnSource>(),
                0,
                0,
                0,
                0,
                Array.Empty<string>());
        }

        var sources = new List<LoadedPgnSource>(filePaths.Count);
        var diagnostics = new List<string>();
        var parseMilliseconds = 0d;
        var semanticMilliseconds = 0d;
        var nodeCount = 0;

        foreach (var path in filePaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            var text = PgnTextDecoder.Decode(bytes);
            if (string.IsNullOrWhiteSpace(text))
            {
                diagnostics.Add($"{Path.GetFileName(path)}: file is empty.");
                continue;
            }

            var stopwatch = Stopwatch.StartNew();
            var document = await _parser.ParseAsync(text, cancellationToken).ConfigureAwait(false);
            stopwatch.Stop();
            parseMilliseconds += stopwatch.Elapsed.TotalMilliseconds;
            if (document.Games.Count == 0)
            {
                diagnostics.Add($"{Path.GetFileName(path)}: no PGN game was found.");
                continue;
            }

            stopwatch.Restart();
            var semantic = await _semanticEnricher.EnrichAsync(document, cancellationToken).ConfigureAwait(false);
            var repair = await Task.Run(
                () => _embeddedCommentRepair.Repair(document, cancellationToken),
                cancellationToken).ConfigureAwait(false);
            if (repair.MovesRepaired > 0)
            {
                semantic = await _semanticEnricher.EnrichAsync(document, cancellationToken).ConfigureAwait(false);
            }
            stopwatch.Stop();
            semanticMilliseconds += stopwatch.Elapsed.TotalMilliseconds;
            nodeCount += document.NodeCount;

            diagnostics.AddRange(document.Diagnostics.Select(diagnostic =>
                $"{Path.GetFileName(path)}:{diagnostic.Line}:{diagnostic.Column} {diagnostic.Message}"));
            diagnostics.AddRange(semantic.Diagnostics.Select(diagnostic =>
                $"{Path.GetFileName(path)} [{diagnostic.San}] {diagnostic.Message}"));
            sources.Add(new LoadedPgnSource(
                Path.GetFullPath(path),
                Path.GetFileName(path),
                document,
                semantic,
                repair));
        }

        return new LoadedPgnBatch(
            sources,
            sources.Sum(static source => source.Document.Games.Count),
            nodeCount,
            parseMilliseconds,
            semanticMilliseconds,
            diagnostics);
    }
}

public sealed record LoadedPgnSource(
    string FilePath,
    string FileName,
    PgnDocument Document,
    PgnSemanticResult SemanticResult,
    EmbeddedCommentMoveRepairStats? EmbeddedMoveRepair = null);

public sealed record LoadedPgnBatch(
    IReadOnlyList<LoadedPgnSource> Sources,
    int GameCount,
    int NodeCount,
    double ParseMilliseconds,
    double SemanticMilliseconds,
    IReadOnlyList<string> Diagnostics);

public static class PgnTextDecoder
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly char[] Windows1252Controls =
    [
        '\u20AC', '\u0081', '\u201A', '\u0192', '\u201E', '\u2026', '\u2020', '\u2021',
        '\u02C6', '\u2030', '\u0160', '\u2039', '\u0152', '\u008D', '\u017D', '\u008F',
        '\u0090', '\u2018', '\u2019', '\u201C', '\u201D', '\u2022', '\u2013', '\u2014',
        '\u02DC', '\u2122', '\u0161', '\u203A', '\u0153', '\u009D', '\u017E', '\u0178',
    ];

    public static string Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            bytes = bytes[3..];
        }

        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return DecodeWindows1252(bytes);
        }
    }

    private static string DecodeWindows1252(ReadOnlySpan<byte> bytes) =>
        string.Create(
            bytes.Length,
            bytes.ToArray(),
            static (target, source) =>
            {
                for (var index = 0; index < source.Length; index++)
                {
                    var value = source[index];
                    target[index] = value is >= 0x80 and <= 0x9F
                        ? Windows1252Controls[value - 0x80]
                        : (char)value;
                }
            });
}
