using System.Text;
using ChessMentor.Viewer;

namespace ChessMentor.Tests;

public sealed class PgnTextDecoderTests
{
    [Fact]
    public void Utf8BomAndWindows1252AreDecodedDeterministically()
    {
        var utf8 = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("{فارسی}")).ToArray();
        Assert.Equal("{فارسی}", PgnTextDecoder.Decode(utf8));

        byte[] windows1252 = [0x93, (byte)'A', 0x94];
        Assert.Equal("“A”", PgnTextDecoder.Decode(windows1252));
    }

    [Fact]
    public async Task MultiFileLoaderKeepsEveryTopLevelGame()
    {
        var token = TestContext.Current.CancellationToken;
        var directory = Path.Combine(Path.GetTempPath(), "ChessMentor.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var first = Path.Combine(directory, "first.pgn");
        var second = Path.Combine(directory, "second.pgn");
        try
        {
            await File.WriteAllTextAsync(
                first,
                "[Event \"One\"]\n\n1. e4 *\n\n[Event \"Two\"]\n\n1. d4 *\n",
                token);
            await File.WriteAllTextAsync(second, "[Event \"Three\"]\n\n1. c4 *\n", token);

            var batch = await new ViewerDocumentLoader().LoadAsync(new[] { first, second }, token);

            Assert.Equal(2, batch.Sources.Count);
            Assert.Equal(3, batch.GameCount);
            Assert.Equal(3, batch.NodeCount);
            Assert.Empty(batch.Diagnostics);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
