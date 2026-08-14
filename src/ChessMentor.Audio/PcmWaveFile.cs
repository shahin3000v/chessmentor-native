using System.Text;

namespace ChessMentor.Audio;

public static class PcmWaveFile
{
    public const ushort Channels = 1;
    public const uint SampleRate = 44_100;
    public const ushort BitsPerSample = 16;
    public const ushort BlockAlignment = Channels * (BitsPerSample / 8);
    public const uint BytesPerSecond = SampleRate * BlockAlignment;
    public const int HeaderLength = 44;

    public static void WriteHeader(Stream stream, long dataLength)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanSeek || !stream.CanWrite)
        {
            throw new ArgumentException("The WAV stream must be seekable and writable.", nameof(stream));
        }

        if (dataLength is < 0 or > uint.MaxValue - 36)
        {
            throw new ArgumentOutOfRangeException(nameof(dataLength));
        }

        var previousPosition = stream.Position;
        stream.Position = 0;
        using (var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true))
        {
            writer.Write("RIFF"u8);
            writer.Write(checked((uint)(36 + dataLength)));
            writer.Write("WAVE"u8);
            writer.Write("fmt "u8);
            writer.Write(16u);
            writer.Write((ushort)1);
            writer.Write(Channels);
            writer.Write(SampleRate);
            writer.Write(BytesPerSecond);
            writer.Write(BlockAlignment);
            writer.Write(BitsPerSample);
            writer.Write("data"u8);
            writer.Write(checked((uint)dataLength));
        }

        stream.Position = Math.Max(previousPosition, HeaderLength);
    }

    public static long DurationMilliseconds(long dataLength)
    {
        if (dataLength < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(dataLength));
        }

        return checked(dataLength * 1000 / BytesPerSecond);
    }
}
