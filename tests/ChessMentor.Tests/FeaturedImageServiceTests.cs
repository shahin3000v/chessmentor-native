using ChessMentor.Studio;

namespace ChessMentor.Tests;

public sealed class FeaturedImageServiceTests
{
    [Theory]
    [InlineData("89504E470D0A1A0A00000000", "image/png", ".png")]
    [InlineData("FFD8FF000000000000000000", "image/jpeg", ".jpg")]
    [InlineData("524946460000000057454250", "image/webp", ".webp")]
    public void SignatureDetectionAcceptsOnlySupportedImageKinds(
        string hexadecimal,
        string expectedMimeType,
        string expectedExtension)
    {
        var format = FeaturedImageService.Identify(Convert.FromHexString(hexadecimal));

        Assert.Equal(expectedMimeType, format.MimeType);
        Assert.Equal(expectedExtension, format.Extension);
    }

    [Fact]
    public void InvalidOrOversizedInputIsRejectedBeforePersistence()
    {
        Assert.Throws<InvalidDataException>(() => FeaturedImageService.Identify("not an image"u8));
        Assert.Throws<InvalidDataException>(() =>
            FeaturedImageService.Identify(new byte[FeaturedImageService.MaxImageBytes + 1]));
    }

    [Fact]
    public async Task InstalledImageProducesACompatibleDataUri()
    {
        var token = TestContext.Current.CancellationToken;
        var directory = Path.Combine(Path.GetTempPath(), "ChessMentor.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var source = Path.Combine(directory, "cover.png");
        var bytes = Convert.FromHexString("89504E470D0A1A0A01020304");
        await File.WriteAllBytesAsync(source, bytes, token);

        var installed = await FeaturedImageService.InstallAsync(source, token);
        var dataUri = await FeaturedImageService.ToDataUriAsync(installed.FilePath, token);

        Assert.Equal("cover.png", installed.FileName);
        Assert.Equal("image/png", installed.MimeType);
        Assert.StartsWith("data:image/png;base64,", dataUri);
        Assert.Equal(bytes, Convert.FromBase64String(dataUri[(dataUri.IndexOf(',') + 1)..]));
    }
}
