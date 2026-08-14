using ChessMentor.Core.Mvvm;

namespace ChessMentor.Core.Diagnostics;

public sealed class PerformanceSnapshot : ObservableObject
{
    private double _pgnParseMilliseconds;
    private double _pgnSemanticMilliseconds;
    private int _gameCount;
    private int _nodeCount;
    private double _renderMilliseconds;
    private double _databaseMilliseconds;
    private int _translationQueued;
    private int _translationRunning;
    private long _managedMemoryBytes;

    public double PgnParseMilliseconds { get => _pgnParseMilliseconds; set => SetProperty(ref _pgnParseMilliseconds, value); }
    public double PgnSemanticMilliseconds { get => _pgnSemanticMilliseconds; set => SetProperty(ref _pgnSemanticMilliseconds, value); }
    public int GameCount { get => _gameCount; set => SetProperty(ref _gameCount, value); }
    public int NodeCount { get => _nodeCount; set => SetProperty(ref _nodeCount, value); }
    public double RenderMilliseconds { get => _renderMilliseconds; set => SetProperty(ref _renderMilliseconds, value); }
    public double DatabaseMilliseconds { get => _databaseMilliseconds; set => SetProperty(ref _databaseMilliseconds, value); }
    public int TranslationQueued { get => _translationQueued; set => SetProperty(ref _translationQueued, value); }
    public int TranslationRunning { get => _translationRunning; set => SetProperty(ref _translationRunning, value); }
    public long ManagedMemoryBytes { get => _managedMemoryBytes; set => SetProperty(ref _managedMemoryBytes, value); }

    public void RefreshMemory() => ManagedMemoryBytes = GC.GetTotalMemory(forceFullCollection: false);
}
