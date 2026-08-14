using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace ChessMentor.Translation;

public static class PhraseIdentity
{
    private static readonly HashSet<char> RemovedFormatMarks =
    [
        '\u200b', '\u200c', '\u200d', '\u200e', '\u200f',
        '\u202a', '\u202b', '\u202c', '\u202d', '\u202e',
        '\u2060', '\u2066', '\u2067', '\u2068', '\u2069', '\ufeff',
    ];

    public static string Create(string sourceText)
    {
        var normalized = NormalizeSource(sourceText);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static string NormalizeSource(string sourceText)
    {
        ArgumentNullException.ThrowIfNull(sourceText);
        var value = sourceText.Normalize(NormalizationForm.FormKC);
        var output = new StringBuilder(value.Length);
        var pendingWhitespace = false;
        foreach (var original in value)
        {
            if (RemovedFormatMarks.Contains(original))
            {
                continue;
            }

            var character = original switch
            {
                '\ue028' or '\uf028' => '♘',
                '\u2018' or '\u2019' => '\'',
                '\u201c' or '\u201d' => '"',
                '\u2013' or '\u2014' => '-',
                '\u00a0' => ' ',
                _ => original,
            };
            if (character == '…')
            {
                AppendPendingSpace(output, ref pendingWhitespace);
                output.Append("...");
                continue;
            }

            if (char.IsWhiteSpace(character))
            {
                pendingWhitespace = output.Length > 0;
                continue;
            }

            AppendPendingSpace(output, ref pendingWhitespace);
            output.Append(character);
        }

        return output.ToString().Trim().ToLower(CultureInfo.InvariantCulture);
    }

    public static bool ShouldTranslate(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var english = 0;
        var persian = 0;
        var insideCommand = false;
        for (var index = 0; index < text.Length; index++)
        {
            if (!insideCommand && text[index] == '[' && index + 1 < text.Length && text[index + 1] == '%')
            {
                insideCommand = true;
                index++;
                continue;
            }

            if (insideCommand)
            {
                insideCommand = text[index] != ']';
                continue;
            }

            var character = text[index];
            if (character is >= 'A' and <= 'Z' or >= 'a' and <= 'z')
            {
                english++;
            }
            else if (character is >= '\u0600' and <= '\u06ff')
            {
                persian++;
            }
        }

        return english >= 3 && english > persian;
    }

    private static void AppendPendingSpace(StringBuilder output, ref bool pendingWhitespace)
    {
        if (pendingWhitespace && output.Length > 0)
        {
            output.Append(' ');
        }

        pendingWhitespace = false;
    }
}
