namespace ChessMentor.Audio;

public enum MoveSoundKind
{
    Move,
    Capture,
    Castle,
    Check,
}

public interface IMoveSoundService : IDisposable
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
    void Play(MoveSoundKind kind);
    void Stop();
}

public static class MoveSoundClassifier
{
    public static MoveSoundKind FromSan(string? san)
    {
        var value = san ?? string.Empty;
        if (value.EndsWith('+') || value.EndsWith('#'))
        {
            return MoveSoundKind.Check;
        }

        if (value.StartsWith("O-O", StringComparison.Ordinal) ||
            value.StartsWith("0-0", StringComparison.Ordinal))
        {
            return MoveSoundKind.Castle;
        }

        return value.Contains('x') ? MoveSoundKind.Capture : MoveSoundKind.Move;
    }
}

public sealed record MoveAudioRecording(string FilePath, string ContentType, long DurationMilliseconds);

public sealed class MoveAudioPlaybackState(
    bool isPlaying,
    long positionMilliseconds,
    long durationMilliseconds,
    string? error = null) : EventArgs
{
    public bool IsPlaying { get; } = isPlaying;
    public long PositionMilliseconds { get; } = positionMilliseconds;
    public long DurationMilliseconds { get; } = durationMilliseconds;
    public string? Error { get; } = error;
}

public interface IMoveAudioRecorder : IDisposable
{
    bool IsRecording { get; }
    Task StartAsync(string targetPath, CancellationToken cancellationToken = default);
    Task<MoveAudioRecording> StopAsync(CancellationToken cancellationToken = default);
    void Cancel();
}

public interface IMoveAudioPlayer : IDisposable
{
    event EventHandler<MoveAudioPlaybackState>? StateChanged;
    Task OpenAsync(string filePath, bool autoplay, CancellationToken cancellationToken = default);
    void Toggle();
    void Seek(long positionMilliseconds);
    void Stop();
}
