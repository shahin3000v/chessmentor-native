using System.IO;
using System.Runtime.InteropServices;
using ChessMentor.Audio;

namespace ChessMentor.Desktop.Services;

/// <summary>
/// Captures the Windows default microphone into a mono PCM WAV file. Completed
/// native buffers are drained by one background worker; no microphone or disk
/// operation enters the WPF dispatcher or the chess-board render path.
/// </summary>
public sealed partial class NativeWaveRecorder : IMoveAudioRecorder
{
    private const uint WaveMapper = 0xFFFFFFFF;
    private const uint CallbackEvent = 0x00050000;
    private const uint HeaderDone = 0x00000001;
    private const int BufferCount = 4;
    private const int BufferLength = 32 * 1024;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _nativeGate = new();
    private nint _device;
    private FileStream? _stream;
    private List<NativeBuffer>? _buffers;
    private AutoResetEvent? _bufferReady;
    private Task? _captureTask;
    private string? _targetPath;
    private Exception? _captureError;
    private long _dataLength;
    private int _acceptSamples;
    private bool _disposed;

    public bool IsRecording => Volatile.Read(ref _acceptSamples) != 0;

    public async Task StartAsync(string targetPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
        ObjectDisposedException.ThrowIf(_disposed, this);
        var fullPath = Path.GetFullPath(targetPath);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_device != 0 || IsRecording)
            {
                throw new InvalidOperationException("A move-audio recording is already active.");
            }

            await Task.Run(() => StartCore(fullPath, cancellationToken), cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<MoveAudioRecording> StopAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_device == 0 || _targetPath is null)
            {
                throw new InvalidOperationException("No move-audio recording is active.");
            }

            return await Task.Run(StopAndSaveCore, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Cancel()
    {
        if (_disposed && _device == 0)
        {
            return;
        }

        _gate.Wait();
        try
        {
            CancelCore(deleteFile: true);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Cancel();
        _disposed = true;
        _gate.Dispose();
    }

    private void StartCore(string fullPath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (WaveInGetNumDevs() == 0)
        {
            throw new InvalidOperationException(
                "هیچ میکروفون فعالی در ویندوز پیدا نشد. یک Input Device را در Settings > System > Sound فعال کنید.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException("Audio target directory is invalid."));

        FileStream? stream = null;
        AutoResetEvent? bufferReady = null;
        var buffers = new List<NativeBuffer>(BufferCount);
        nint device = 0;
        try
        {
            stream = new FileStream(
                fullPath,
                FileMode.Create,
                FileAccess.ReadWrite,
                FileShare.Read,
                BufferLength,
                FileOptions.SequentialScan);
            PcmWaveFile.WriteHeader(stream, 0);

            bufferReady = new AutoResetEvent(false);
            var format = WaveFormat.CreatePcm();
            ThrowIfWaveError(WaveInOpen(
                out device,
                WaveMapper,
                ref format,
                bufferReady.SafeWaitHandle.DangerousGetHandle(),
                0,
                CallbackEvent), "باز کردن میکروفون");

            for (var index = 0; index < BufferCount; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var buffer = new NativeBuffer(BufferLength);
                buffers.Add(buffer);
                ThrowIfWaveError(
                    WaveInPrepareHeader(device, buffer.HeaderPointer, (uint)Marshal.SizeOf<WaveHeader>()),
                    "آماده‌سازی حافظهٔ ضبط");
                buffer.IsPrepared = true;
                ThrowIfWaveError(
                    WaveInAddBuffer(device, buffer.HeaderPointer, (uint)Marshal.SizeOf<WaveHeader>()),
                    "ارسال حافظه به میکروفون");
            }

            _device = device;
            _stream = stream;
            _buffers = buffers;
            _bufferReady = bufferReady;
            _targetPath = fullPath;
            _captureError = null;
            _dataLength = 0;
            Volatile.Write(ref _acceptSamples, 1);
            _captureTask = Task.Run(() => CaptureLoop(device, stream, buffers, bufferReady));

            ThrowIfWaveError(WaveInStart(device), "شروع ضبط");
            stream = null;
            bufferReady = null;
            device = 0;
            buffers = [];
        }
        catch
        {
            Volatile.Write(ref _acceptSamples, 0);
            if (_device != 0)
            {
                CancelCore(deleteFile: true);
                stream = null;
                bufferReady = null;
                device = 0;
                buffers = [];
            }
            else if (device != 0)
            {
                _ = WaveInReset(device);
                ReleaseBuffers(device, buffers);
                _ = WaveInClose(device);
            }
            else
            {
                DisposeBuffers(buffers);
            }

            stream?.Dispose();
            bufferReady?.Dispose();
            TryDelete(fullPath);
            throw;
        }
    }

    private void CaptureLoop(
        nint device,
        FileStream stream,
        IReadOnlyList<NativeBuffer> buffers,
        AutoResetEvent bufferReady)
    {
        try
        {
            while (true)
            {
                bufferReady.WaitOne();
                var continueRecording = Volatile.Read(ref _acceptSamples) != 0;
                DrainCompletedBuffers(device, stream, buffers, continueRecording);
                if (!continueRecording || Volatile.Read(ref _acceptSamples) == 0)
                {
                    return;
                }
            }
        }
        catch (Exception exception)
        {
            _captureError ??= exception;
            Volatile.Write(ref _acceptSamples, 0);
        }
    }

    private void DrainCompletedBuffers(
        nint device,
        Stream stream,
        IReadOnlyList<NativeBuffer> buffers,
        bool requeue)
    {
        foreach (var buffer in buffers)
        {
            var header = Marshal.PtrToStructure<WaveHeader>(buffer.HeaderPointer);
            if ((header.Flags & HeaderDone) == 0)
            {
                continue;
            }

            var count = checked((int)Math.Min(header.BytesRecorded, header.BufferLength));
            if (count > 0)
            {
                var samples = GC.AllocateUninitializedArray<byte>(count);
                Marshal.Copy(header.Data, samples, 0, count);
                stream.Write(samples);
                _dataLength += count;
            }

            if (requeue)
            {
                lock (_nativeGate)
                {
                    if (Volatile.Read(ref _acceptSamples) != 0)
                    {
                        ThrowIfWaveError(
                            WaveInAddBuffer(device, buffer.HeaderPointer, (uint)Marshal.SizeOf<WaveHeader>()),
                            "ادامهٔ ضبط");
                    }
                }
            }
        }
    }

    private MoveAudioRecording StopAndSaveCore()
    {
        var path = _targetPath
            ?? throw new InvalidOperationException("Recording target is missing.");
        Exception? stopError = null;
        var device = _device;
        lock (_nativeGate)
        {
            Volatile.Write(ref _acceptSamples, 0);
            if (device != 0)
            {
                stopError = WaveErrorOrNull(WaveInStop(device), "توقف ضبط");
                stopError ??= WaveErrorOrNull(WaveInReset(device), "تخلیهٔ حافظهٔ ضبط");
            }
        }

        _bufferReady?.Set();
        try
        {
            _captureTask?.GetAwaiter().GetResult();
        }
        catch (Exception exception)
        {
            _captureError ??= exception;
        }

        var dataLength = _dataLength;
        var stream = _stream;
        var buffers = _buffers ?? [];
        var bufferReady = _bufferReady;
        var captureError = _captureError;
        ClearCaptureState();

        if (device != 0)
        {
            ReleaseBuffers(device, buffers);
            stopError ??= WaveErrorOrNull(WaveInClose(device), "بستن میکروفون");
        }

        bufferReady?.Dispose();
        try
        {
            if (stream is not null)
            {
                PcmWaveFile.WriteHeader(stream, dataLength);
                stream.Flush(flushToDisk: true);
            }
        }
        finally
        {
            stream?.Dispose();
        }

        var error = captureError ?? stopError;
        if (error is not null)
        {
            TryDelete(path);
            throw new InvalidOperationException("ضبط صدا کامل نشد. " + error.Message, error);
        }

        if (dataLength == 0)
        {
            TryDelete(path);
            throw new InvalidDataException(
                "هیچ نمونهٔ صوتی از میکروفون دریافت نشد. دسترسی Microphone را در Settings > Privacy & security > Microphone برای Desktop apps فعال کنید.");
        }

        return new MoveAudioRecording(
            path,
            "audio/wav",
            PcmWaveFile.DurationMilliseconds(dataLength));
    }

    private void CancelCore(bool deleteFile)
    {
        var device = _device;
        lock (_nativeGate)
        {
            Volatile.Write(ref _acceptSamples, 0);
            if (device != 0)
            {
                _ = WaveInStop(device);
                _ = WaveInReset(device);
            }
        }

        _bufferReady?.Set();
        try
        {
            _captureTask?.GetAwaiter().GetResult();
        }
        catch
        {
            // Cancellation intentionally discards capture failures with the file.
        }

        var stream = _stream;
        var buffers = _buffers ?? [];
        var bufferReady = _bufferReady;
        var path = _targetPath;
        ClearCaptureState();

        if (device != 0)
        {
            ReleaseBuffers(device, buffers);
            _ = WaveInClose(device);
        }

        stream?.Dispose();
        bufferReady?.Dispose();
        if (deleteFile && path is not null)
        {
            TryDelete(path);
        }
    }

    private void ClearCaptureState()
    {
        _device = 0;
        _stream = null;
        _buffers = null;
        _bufferReady = null;
        _captureTask = null;
        _targetPath = null;
        _captureError = null;
        _dataLength = 0;
    }

    private static void ReleaseBuffers(nint device, IEnumerable<NativeBuffer> buffers)
    {
        foreach (var buffer in buffers)
        {
            if (buffer.IsPrepared)
            {
                _ = WaveInUnprepareHeader(
                    device,
                    buffer.HeaderPointer,
                    (uint)Marshal.SizeOf<WaveHeader>());
            }

            buffer.Dispose();
        }
    }

    private static void DisposeBuffers(IEnumerable<NativeBuffer> buffers)
    {
        foreach (var buffer in buffers)
        {
            buffer.Dispose();
        }
    }

    private static void ThrowIfWaveError(uint result, string operation)
    {
        if (result != 0)
        {
            throw CreateWaveError(result, operation);
        }
    }

    private static Exception? WaveErrorOrNull(uint result, string operation) =>
        result == 0 ? null : CreateWaveError(result, operation);

    private static InvalidOperationException CreateWaveError(uint result, string operation)
    {
        var buffer = Marshal.AllocHGlobal(512 * sizeof(char));
        try
        {
            var description = WaveInGetErrorText(result, buffer, 512) == 0
                ? Marshal.PtrToStringUni(buffer)
                : null;
            return new InvalidOperationException(
                $"{operation} ممکن نشد (WinMM {result}{(string.IsNullOrWhiteSpace(description) ? string.Empty : $": {description}")}). " +
                "ورودی پیش‌فرض، مجوز میکروفون و استفاده‌نشدن انحصاری آن توسط برنامه‌ای دیگر را بررسی کنید.");
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WaveFormat
    {
        public ushort FormatTag;
        public ushort Channels;
        public uint SamplesPerSecond;
        public uint AverageBytesPerSecond;
        public ushort BlockAlignment;
        public ushort BitsPerSample;
        public ushort ExtraSize;

        public static WaveFormat CreatePcm() => new()
        {
            FormatTag = 1,
            Channels = PcmWaveFile.Channels,
            SamplesPerSecond = PcmWaveFile.SampleRate,
            AverageBytesPerSecond = PcmWaveFile.BytesPerSecond,
            BlockAlignment = PcmWaveFile.BlockAlignment,
            BitsPerSample = PcmWaveFile.BitsPerSample,
            ExtraSize = 0,
        };
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WaveHeader
    {
        public nint Data;
        public uint BufferLength;
        public uint BytesRecorded;
        public nuint User;
        public uint Flags;
        public uint Loops;
        public nint Next;
        public nuint Reserved;
    }

    private sealed class NativeBuffer : IDisposable
    {
        public NativeBuffer(int length)
        {
            DataPointer = Marshal.AllocHGlobal(length);
            HeaderPointer = Marshal.AllocHGlobal(Marshal.SizeOf<WaveHeader>());
            Marshal.StructureToPtr(new WaveHeader
            {
                Data = DataPointer,
                BufferLength = checked((uint)length),
            }, HeaderPointer, false);
        }

        public nint DataPointer { get; private set; }
        public nint HeaderPointer { get; private set; }
        public bool IsPrepared { get; set; }

        public void Dispose()
        {
            if (HeaderPointer != 0)
            {
                Marshal.FreeHGlobal(HeaderPointer);
                HeaderPointer = 0;
            }

            if (DataPointer != 0)
            {
                Marshal.FreeHGlobal(DataPointer);
                DataPointer = 0;
            }
        }
    }

    [LibraryImport("winmm.dll", EntryPoint = "waveInGetNumDevs")]
    private static partial uint WaveInGetNumDevs();

    [LibraryImport("winmm.dll", EntryPoint = "waveInOpen")]
    private static partial uint WaveInOpen(
        out nint waveIn,
        uint deviceId,
        ref WaveFormat format,
        nint eventHandle,
        nint instance,
        uint flags);

    [LibraryImport("winmm.dll", EntryPoint = "waveInPrepareHeader")]
    private static partial uint WaveInPrepareHeader(nint waveIn, nint header, uint headerSize);

    [LibraryImport("winmm.dll", EntryPoint = "waveInUnprepareHeader")]
    private static partial uint WaveInUnprepareHeader(nint waveIn, nint header, uint headerSize);

    [LibraryImport("winmm.dll", EntryPoint = "waveInAddBuffer")]
    private static partial uint WaveInAddBuffer(nint waveIn, nint header, uint headerSize);

    [LibraryImport("winmm.dll", EntryPoint = "waveInStart")]
    private static partial uint WaveInStart(nint waveIn);

    [LibraryImport("winmm.dll", EntryPoint = "waveInStop")]
    private static partial uint WaveInStop(nint waveIn);

    [LibraryImport("winmm.dll", EntryPoint = "waveInReset")]
    private static partial uint WaveInReset(nint waveIn);

    [LibraryImport("winmm.dll", EntryPoint = "waveInClose")]
    private static partial uint WaveInClose(nint waveIn);

    [LibraryImport("winmm.dll", EntryPoint = "waveInGetErrorTextW")]
    private static partial uint WaveInGetErrorText(uint error, nint text, uint textLength);
}
