using System.Text.RegularExpressions;

namespace ChessMentor.Viewer;

public static partial class ProgressiveAnswerParser
{
    public static ProgressiveAnswerDocument Parse(string? text)
    {
        var display = text ?? string.Empty;
        var markers = AnswerMarkerRegex().Matches(display)
            .Select(match =>
            {
                var prefixLength = match.Groups[1].Length;
                var start = match.Index + prefixLength;
                var label = match.Groups[2].Value;
                return new AnswerMarker(start, start + label.Length, label);
            })
            .ToArray();
        if (markers.Length == 0)
        {
            return new ProgressiveAnswerDocument(display, Array.Empty<ProgressiveAnswerSection>());
        }

        var sections = new ProgressiveAnswerSection[markers.Length];
        for (var index = 0; index < markers.Length; index++)
        {
            var marker = markers[index];
            var contentEnd = index + 1 < markers.Length ? markers[index + 1].Start : display.Length;
            sections[index] = new ProgressiveAnswerSection(marker.Label, display[marker.End..contentEnd]);
        }

        return new ProgressiveAnswerDocument(display[..markers[0].Start], sections);
    }

    [GeneratedRegex(@"(^|[\s([{«])(پاسخ(?: :|:)(?![\u0600-\u06FF]))", RegexOptions.CultureInvariant)]
    private static partial Regex AnswerMarkerRegex();

    private readonly record struct AnswerMarker(int Start, int End, string Label);
}

public sealed record ProgressiveAnswerDocument(
    string Before,
    IReadOnlyList<ProgressiveAnswerSection> Sections);

public sealed record ProgressiveAnswerSection(string Label, string Content);
