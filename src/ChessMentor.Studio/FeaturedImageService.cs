namespace ChessMentor.Studio;

public sealed record InstalledFeaturedImage(string FilePath, string FileName, string MimeType);
public sealed record FeaturedImageFormat(string MimeType, string Extension);

public static class FeaturedImageService
{
    public const int MaxImageBytes = 8 * 1024 * 1024;

    public static async Task<InstalledFeaturedImage> InstallAsync(
        string sourcePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        var fullPath = Path.GetFullPath(sourcePath);
        var length = new FileInfo(fullPath).Length;
        if (length is <= 0 or > MaxImageBytes)
        {
            throw new InvalidDataException("حجم تصویر شاخص باید بین ۱ بایت و ۸ مگابایت باشد.");
        }

        var bytes = await File.ReadAllBytesAsync(fullPath, cancellationToken).ConfigureAwait(false);
        var format = Identify(bytes);
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ChessMentor",
            "CourseImages");
        Directory.CreateDirectory(directory);
        var target = Path.Combine(directory, $"featured-{Guid.NewGuid():N}{format.Extension}");
        await File.WriteAllBytesAsync(target, bytes, cancellationToken).ConfigureAwait(false);
        return new InstalledFeaturedImage(target, Path.GetFileName(sourcePath), format.MimeType);
    }

    public static async Task<string> ToDataUriAsync(
        string? installedPath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(installedPath))
        {
            return string.Empty;
        }

        var length = new FileInfo(installedPath).Length;
        if (length is <= 0 or > MaxImageBytes)
        {
            throw new InvalidDataException("حجم تصویر شاخص باید بین ۱ بایت و ۸ مگابایت باشد.");
        }

        var bytes = await File.ReadAllBytesAsync(installedPath, cancellationToken).ConfigureAwait(false);
        var format = Identify(bytes);
        var base64 = await Task.Run(() => Convert.ToBase64String(bytes), cancellationToken).ConfigureAwait(false);
        return $"data:{format.MimeType};base64,{base64}";
    }

    public static FeaturedImageFormat Identify(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length is <= 0 or > MaxImageBytes)
        {
            throw new InvalidDataException("حجم تصویر شاخص باید بین ۱ بایت و ۸ مگابایت باشد.");
        }

        if (bytes.Length >= 8 &&
            bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47 &&
            bytes[4] == 0x0D && bytes[5] == 0x0A && bytes[6] == 0x1A && bytes[7] == 0x0A)
        {
            return new FeaturedImageFormat("image/png", ".png");
        }

        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
        {
            return new FeaturedImageFormat("image/jpeg", ".jpg");
        }

        if (bytes.Length >= 12 &&
            bytes[0] == (byte)'R' && bytes[1] == (byte)'I' && bytes[2] == (byte)'F' && bytes[3] == (byte)'F' &&
            bytes[8] == (byte)'W' && bytes[9] == (byte)'E' && bytes[10] == (byte)'B' && bytes[11] == (byte)'P')
        {
            return new FeaturedImageFormat("image/webp", ".webp");
        }

        throw new InvalidDataException("فرمت تصویر شاخص باید JPEG، PNG یا WebP باشد.");
    }
}
