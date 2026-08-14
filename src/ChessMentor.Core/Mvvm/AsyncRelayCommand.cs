using System.Windows.Input;

namespace ChessMentor.Core.Mvvm;

public sealed class AsyncRelayCommand : ICommand, IDisposable
{
    private readonly Func<CancellationToken, Task> _execute;
    private readonly Func<bool>? _canExecute;
    private CancellationTokenSource? _run;
    private bool _isRunning;

    public AsyncRelayCommand(Func<CancellationToken, Task> execute, Func<bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => !_isRunning && (_canExecute?.Invoke() ?? true);

    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter))
        {
            return;
        }

        _isRunning = true;
        _run = new CancellationTokenSource();
        RaiseCanExecuteChanged();
        try
        {
            await _execute(_run.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (_run.IsCancellationRequested)
        {
        }
        finally
        {
            _run.Dispose();
            _run = null;
            _isRunning = false;
            RaiseCanExecuteChanged();
        }
    }

    public void Cancel() => _run?.Cancel();

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);

    public void Dispose()
    {
        _run?.Cancel();
        _run?.Dispose();
    }
}
