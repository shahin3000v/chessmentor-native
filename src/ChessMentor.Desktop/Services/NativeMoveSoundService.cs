using System.IO;
using System.Windows.Media;
using ChessMentor.Audio;

namespace ChessMentor.Desktop.Services;

/// <summary>
/// Pre-generates tiny PCM wave files off the UI thread and reuses MediaPlayer
/// instances. Board rendering never waits for audio file or codec work.
/// </summary>
public sealed class NativeMoveSoundService : IMoveSoundService
{
    private readonly Dictionary<MoveSoundKind, MediaPlayer> _players = new();
    private bool _disposed;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var cacheDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ChessMentor",
            "AudioCache",
            "viewer-move-sounds-v1");
        var files = await Task.Run(
            () => BuildSoundFiles(cacheDirectory, cancellationToken),
            cancellationToken).ConfigureAwait(true);

        foreach (var (kind, path) in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var player = new MediaPlayer { Volume = 0.52 };
            player.Open(new Uri(path, UriKind.Absolute));
            _players[kind] = player;
        }
    }

    public void Play(MoveSoundKind kind)
    {
        if (_disposed || !_players.TryGetValue(kind, out var player))
        {
            return;
        }

        player.Stop();
        player.Position = TimeSpan.Zero;
        player.Play();
    }

    public void Stop()
    {
        foreach (var player in _players.Values)
        {
            player.Stop();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var player in _players.Values)
        {
            player.Stop();
            player.Close();
        }

        _players.Clear();
    }

    private static IReadOnlyDictionary<MoveSoundKind, string> BuildSoundFiles(
        string cacheDirectory,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(cacheDirectory);
        var definitions = new Dictionary<MoveSoundKind, SoundDefinition>
        {
            [MoveSoundKind.Move] = new(0.085, 520, 350, 0.16),
            [MoveSoundKind.Capture] = new(0.135, 230, 135, 0.32),
            [MoveSoundKind.Castle] = new(0.165, 430, 530, 0.22),
            [MoveSoundKind.Check] = new(0.175, 670, 910, 0.14),
        };
        var paths = new Dictionary<MoveSoundKind, string>();
        foreach (var (kind, definition) in definitions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = Path.Combine(cacheDirectory, $"{kind.ToString().ToLowerInvariant()}.wav");
            if (!File.Exists(path) || new FileInfo(path).Length < 128)
            {
                WriteWave(path, definition, cancellationToken);
            }

            paths[kind] = path;
        }

        return paths;
    }

    private static void WriteWave(
        string path,
        SoundDefinition definition,
        CancellationToken cancellationToken)
    {
        const int sampleRate = 44100;
        const short channels = 1;
        const short bitsPerSample = 16;
        var sampleCount = Math.Max(1, (int)(sampleRate * definition.DurationSeconds));
        var dataLength = sampleCount * sizeof(short);
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        using var writer = new BinaryWriter(stream);
        writer.Write("RIFF"u8);
        writer.Write(36 + dataLength);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write(channels);
        writer.Write(sampleRate);
        writer.Write(sampleRate * channels * bitsPerSample / 8);
        writer.Write((short)(channels * bitsPerSample / 8));
        writer.Write(bitsPerSample);
        writer.Write("data"u8);
        writer.Write(dataLength);

        uint noiseState = 0xC0FFEEu;
        for (var index = 0; index < sampleCount; index++)
        {
            if ((index & 1023) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            var progress = index / (double)sampleCount;
            var envelope = Math.Pow(1d - progress, 2.7);
            var frequency = definition.StartFrequency +
                ((definition.EndFrequency - definition.StartFrequency) * progress);
            var tone = Math.Sin(2d * Math.PI * frequency * index / sampleRate);
            noiseState = (1664525u * noiseState) + 1013904223u;
            var noise = ((noiseState >> 8) / (double)0xFFFFFF - 0.5) * 2d;
            var sample = ((tone * 0.58) + (noise * definition.NoiseMix)) * envelope;
            writer.Write((short)Math.Clamp(sample * short.MaxValue * 0.72, short.MinValue, short.MaxValue));
        }
    }

    private readonly record struct SoundDefinition(
        double DurationSeconds,
        double StartFrequency,
        double EndFrequency,
        double NoiseMix);
}
