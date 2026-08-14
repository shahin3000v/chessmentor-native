using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using ChessMentor.Audio;

namespace ChessMentor.Desktop.Services;

public sealed class NativeMoveAudioPlayer : IMoveAudioPlayer
{
    private readonly MediaPlayer _player = new();
    private readonly DispatcherTimer _timer;
    private bool _isPlaying;
    private bool _disposed;

    public NativeMoveAudioPlayer()
    {
        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(180),
        };
        _timer.Tick += OnTimerTick;
        _player.MediaOpened += OnMediaOpened;
        _player.MediaEnded += OnMediaEnded;
        _player.MediaFailed += OnMediaFailed;
    }

    public event EventHandler<MoveAudioPlaybackState>? StateChanged;

    public Task OpenAsync(
        string filePath,
        bool autoplay,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        _player.Stop();
        _isPlaying = false;
        _player.Open(new Uri(Path.GetFullPath(filePath), UriKind.Absolute));
        if (autoplay)
        {
            _player.Play();
            _isPlaying = true;
            _timer.Start();
        }

        Report();
        return Task.CompletedTask;
    }

    public void Toggle()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_isPlaying)
        {
            _player.Pause();
            _isPlaying = false;
        }
        else
        {
            _player.Play();
            _isPlaying = true;
            _timer.Start();
        }

        Report();
    }

    public void Seek(long positionMilliseconds)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _player.Position = TimeSpan.FromMilliseconds(Math.Max(0, positionMilliseconds));
        Report();
    }

    public void Stop()
    {
        if (_disposed)
        {
            return;
        }

        _player.Stop();
        _player.Position = TimeSpan.Zero;
        _isPlaying = false;
        _timer.Stop();
        Report();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer.Stop();
        _timer.Tick -= OnTimerTick;
        _player.MediaOpened -= OnMediaOpened;
        _player.MediaEnded -= OnMediaEnded;
        _player.MediaFailed -= OnMediaFailed;
        _player.Stop();
        _player.Close();
    }

    private void OnTimerTick(object? sender, EventArgs eventArgs) => Report();

    private void OnMediaOpened(object? sender, EventArgs eventArgs) => Report();

    private void OnMediaEnded(object? sender, EventArgs eventArgs)
    {
        _player.Stop();
        _player.Position = TimeSpan.Zero;
        _isPlaying = false;
        _timer.Stop();
        Report();
    }

    private void OnMediaFailed(object? sender, ExceptionEventArgs eventArgs)
    {
        _isPlaying = false;
        _timer.Stop();
        Report(eventArgs.ErrorException.Message);
    }

    private void Report(string? error = null)
    {
        var duration = _player.NaturalDuration.HasTimeSpan
            ? (long)_player.NaturalDuration.TimeSpan.TotalMilliseconds
            : 0;
        StateChanged?.Invoke(this, new MoveAudioPlaybackState(
            _isPlaying,
            Math.Max(0, (long)_player.Position.TotalMilliseconds),
            Math.Max(0, duration),
            error));
    }
}
