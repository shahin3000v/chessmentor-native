using System.Buffers.Binary;
using System.Text;
using ChessMentor.Audio;

namespace ChessMentor.Tests;

public sealed class PcmWaveFileTests
{
    [Fact]
    public void HeaderContainsExactMonoPcmFormatAndPayloadLength()
    {
        using var stream = new MemoryStream();
        stream.SetLength(PcmWaveFile.HeaderLength + PcmWaveFile.BytesPerSecond);

        PcmWaveFile.WriteHeader(stream, PcmWaveFile.BytesPerSecond);

        var header = stream.ToArray().AsSpan(0, PcmWaveFile.HeaderLength);
        Assert.Equal("RIFF", Encoding.ASCII.GetString(header[..4]));
        Assert.Equal(36u + PcmWaveFile.BytesPerSecond, BinaryPrimitives.ReadUInt32LittleEndian(header[4..8]));
        Assert.Equal("WAVEfmt ", Encoding.ASCII.GetString(header[8..16]));
        Assert.Equal((ushort)1, BinaryPrimitives.ReadUInt16LittleEndian(header[20..22]));
        Assert.Equal(PcmWaveFile.Channels, BinaryPrimitives.ReadUInt16LittleEndian(header[22..24]));
        Assert.Equal(PcmWaveFile.SampleRate, BinaryPrimitives.ReadUInt32LittleEndian(header[24..28]));
        Assert.Equal(PcmWaveFile.BytesPerSecond, BinaryPrimitives.ReadUInt32LittleEndian(header[28..32]));
        Assert.Equal(PcmWaveFile.BlockAlignment, BinaryPrimitives.ReadUInt16LittleEndian(header[32..34]));
        Assert.Equal(PcmWaveFile.BitsPerSample, BinaryPrimitives.ReadUInt16LittleEndian(header[34..36]));
        Assert.Equal("data", Encoding.ASCII.GetString(header[36..40]));
        Assert.Equal(PcmWaveFile.BytesPerSecond, BinaryPrimitives.ReadUInt32LittleEndian(header[40..44]));
    }

    [Fact]
    public void DurationComesFromCapturedBytesInsteadOfWallClock()
    {
        Assert.Equal(1000L, PcmWaveFile.DurationMilliseconds(PcmWaveFile.BytesPerSecond));
        Assert.Equal(250L, PcmWaveFile.DurationMilliseconds(PcmWaveFile.BytesPerSecond / 4));
    }
}
