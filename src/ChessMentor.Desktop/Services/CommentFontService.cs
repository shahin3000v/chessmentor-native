using System.Globalization;
using System.IO;
using System.Windows.Media;

namespace ChessMentor.Desktop.Services;

public sealed record CommentFontSelection(string FamilyName, string FilePath);
public sealed record CommentFontOption(string FamilyName, string Label);

public static class CommentFontService
{
    public const string DefaultFamilyName = "IRANYekanWeb";
    private static readonly Uri ApplicationResourceBase =
        new("pack://application:,,,/", UriKind.Absolute);

    public static IReadOnlyList<CommentFontOption> BuiltInOptions { get; } =
    [
        new("IRANYekanWeb", "ایران یکان"),
        new("IRANSansWeb", "ایران سنس"),
        new("Tahoma", "Tahoma"),
    ];

    public static bool IsBuiltIn(string? familyName) =>
        BuiltInOptions.Any(option =>
            string.Equals(option.FamilyName, familyName, StringComparison.OrdinalIgnoreCase));

    public static async Task<CommentFontSelection> InspectAsync(
        string filePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        var fullPath = Path.GetFullPath(filePath);
        var extension = Path.GetExtension(fullPath);
        if (!string.Equals(extension, ".ttf", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(extension, ".otf", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("فقط فایل‌های TTF و OTF پشتیبانی می‌شوند.");
        }

        return await Task.Run(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!File.Exists(fullPath))
                {
                    throw new FileNotFoundException("فایل فونت پیدا نشد.", fullPath);
                }

                var typeface = new GlyphTypeface(new Uri(fullPath, UriKind.Absolute));
                var familyName = PreferredFamilyName(typeface.FamilyNames);
                if (string.IsNullOrWhiteSpace(familyName))
                {
                    throw new InvalidDataException("نام خانوادهٔ فونت از فایل خوانده نشد.");
                }

                return new CommentFontSelection(familyName, fullPath);
            },
            cancellationToken).ConfigureAwait(false);
    }

    public static async Task<CommentFontSelection> InstallAsync(
        string filePath,
        CancellationToken cancellationToken = default)
    {
        var inspected = await InspectAsync(filePath, cancellationToken).ConfigureAwait(false);
        return await Task.Run(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var directory = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "ChessMentor",
                    "Fonts");
                Directory.CreateDirectory(directory);
                var target = Path.Combine(
                    directory,
                    $"custom-{Guid.NewGuid():N}{Path.GetExtension(inspected.FilePath).ToLowerInvariant()}");
                File.Copy(inspected.FilePath, target, overwrite: false);
                return inspected with { FilePath = target };
            },
            cancellationToken).ConfigureAwait(false);
    }

    public static FontFamily Resolve(string? familyName, string? customFilePath)
    {
        var cleanFamily = string.IsNullOrWhiteSpace(familyName) ? DefaultFamilyName : familyName.Trim();
        if (!string.IsNullOrWhiteSpace(customFilePath) && File.Exists(customFilePath))
        {
            try
            {
                var directory = Path.GetDirectoryName(Path.GetFullPath(customFilePath));
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    var directoryUri = new Uri(
                        directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                        Path.DirectorySeparatorChar,
                        UriKind.Absolute);
                    return new FontFamily(directoryUri, $"./#{cleanFamily}, Tahoma, Segoe UI");
                }
            }
            catch (Exception exception) when (exception is ArgumentException or UriFormatException or IOException)
            {
                // A moved or unreadable custom file safely falls back to an installed family.
            }
        }

        if (string.Equals(cleanFamily, "IRANYekanWeb", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(cleanFamily, "IRANSansWeb", StringComparison.OrdinalIgnoreCase))
        {
            return new FontFamily(
                ApplicationResourceBase,
                $"./Assets/Fonts/#{cleanFamily}, Tahoma, Segoe UI");
        }

        return new FontFamily($"{cleanFamily}, Tahoma, Segoe UI");
    }

    private static string PreferredFamilyName(IDictionary<CultureInfo, string> names)
    {
        foreach (var culture in new[]
                 {
                     CultureInfo.GetCultureInfo("fa-IR"),
                     CultureInfo.GetCultureInfo("en-US"),
                     CultureInfo.InvariantCulture,
                 })
        {
            if (names.TryGetValue(culture, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return names.Values.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
    }
}
