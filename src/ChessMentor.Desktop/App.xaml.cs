using System.IO;
using System.Windows;
using System.Windows.Threading;
using ChessMentor.Chess;
using ChessMentor.Desktop.Services;
using ChessMentor.Desktop.ViewModels;
using ChessMentor.Persistence;
using ChessMentor.Pgn;
using ChessMentor.Viewer;

namespace ChessMentor.Desktop;

public partial class App : Application
{
    private AppDatabase? _database;
    private SettingsRepository? _settingsRepository;
    private ViewerDocumentLoader? _documentLoader;
    private ManagedChessRules? _chessRules;
    private StudioWindow? _studioWindow;
    private bool _studioOpening;

    public App()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var applicationDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ChessMentor");
        _database = new AppDatabase(Path.Combine(applicationDirectory, "chessmentor.db"));
        _chessRules = ManagedChessRules.Instance;
        _documentLoader = new ViewerDocumentLoader(
            new PgnParser(),
            new PgnSemanticEnricher(ManagedChessRules.Instance));
        _settingsRepository = new SettingsRepository(_database);
        var viewModel = new MainWindowViewModel(
            _database,
            new DatabaseUpgradeService(_database),
            _settingsRepository,
            _documentLoader,
            _chessRules,
            new NativeMoveSoundService());
        var window = new MainWindow(viewModel, OpenStudio);
        MainWindow = window;
        window.Show();
        await viewModel.InitializeAsync().ConfigureAwait(true);
    }

    private async void OpenStudio()
    {
        if (_studioWindow is { IsVisible: true })
        {
            if (_studioWindow.WindowState == WindowState.Minimized)
            {
                _studioWindow.WindowState = WindowState.Normal;
            }

            _studioWindow.Activate();
            return;
        }

        if (_studioOpening)
        {
            return;
        }

        if (_database is null || _settingsRepository is null || _documentLoader is null || _chessRules is null)
        {
            return;
        }

        _studioOpening = true;
        StudioWindowViewModel? viewModel = null;
        StudioWindow? window = null;
        try
        {
            // Finish the click event and render pending Viewer work first.
            await System.Windows.Threading.Dispatcher.Yield(DispatcherPriority.ContextIdle);
            viewModel = new StudioWindowViewModel(
                _database,
                _settingsRepository,
                _documentLoader,
                _chessRules,
                new NativeMoveSoundService(),
                new NativeWaveRecorder(),
                new NativeMoveAudioPlayer());
            window = new StudioWindow(viewModel);
            if (MainWindow is { } owner)
            {
                window.Owner = owner;
            }

            _studioWindow = window;
            window.Closed += (_, _) => _studioWindow = null;
            window.Show();

            // Let WPF present the native shell before draft/database startup work.
            await System.Windows.Threading.Dispatcher.Yield(DispatcherPriority.ContextIdle);
            if (window.IsVisible)
            {
                await viewModel.InitializeAsync().ConfigureAwait(true);
            }
        }
        catch (Exception exception)
        {
            DesktopDiagnosticLog.Write("Open PGN Studio", exception);
            if (ReferenceEquals(_studioWindow, window))
            {
                _studioWindow = null;
            }

            viewModel?.Dispose();

            MessageBox.Show(
                $"PGN Studio باز نشد.\n\n{exception.Message}\n\nلاگ کامل:\n{DesktopDiagnosticLog.FilePath}",
                "خطای PGN Studio",
                MessageBoxButton.OK,
                MessageBoxImage.Error,
                MessageBoxResult.OK,
                MessageBoxOptions.RightAlign | MessageBoxOptions.RtlReading);
        }
        finally
        {
            _studioOpening = false;
        }
    }

    private static void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs eventArgs)
    {
        if (eventArgs.ExceptionObject is Exception exception)
        {
            DesktopDiagnosticLog.Write("AppDomain unhandled exception", exception);
        }
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs eventArgs)
    {
        DesktopDiagnosticLog.Write("Unobserved task exception", eventArgs.Exception);
        eventArgs.SetObserved();
    }

    private void OnDispatcherUnhandledException(
        object sender,
        System.Windows.Threading.DispatcherUnhandledExceptionEventArgs eventArgs)
    {
        DesktopDiagnosticLog.Write("WPF dispatcher exception", eventArgs.Exception);
        if (eventArgs.Exception is OutOfMemoryException)
        {
            return;
        }

        eventArgs.Handled = true;
        MessageBox.Show(
            $"یک خطای رابط کاربری مهار شد و برنامه بسته نشد.\n\n{eventArgs.Exception.Message}\n\nلاگ کامل:\n{DesktopDiagnosticLog.FilePath}",
            "خطای ChessMentor",
            MessageBoxButton.OK,
            MessageBoxImage.Error,
            MessageBoxResult.OK,
            MessageBoxOptions.RightAlign | MessageBoxOptions.RtlReading);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        DispatcherUnhandledException -= OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException -= OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;
        if (_database is not null)
        {
            _database.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        base.OnExit(e);
    }
}
