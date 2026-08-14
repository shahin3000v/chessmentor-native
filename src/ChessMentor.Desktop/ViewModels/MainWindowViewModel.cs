using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using ChessMentor.Audio;
using ChessMentor.Chess;
using ChessMentor.Core.Diagnostics;
using ChessMentor.Core.Mvvm;
using ChessMentor.Desktop.Controls;
using ChessMentor.Desktop.Services;
using ChessMentor.Persistence;
using ChessMentor.Pgn;
using ChessMentor.Viewer;

namespace ChessMentor.Desktop.ViewModels;

public sealed class MainWindowViewModel : ObservableObject, IDisposable
{
    private static readonly IReadOnlyList<int> AllowedCommentFontSizes =
        [12, 13, 14, 15, 16, 18, 20, 22, 24, 26, 28];

    private readonly AppDatabase _database;
    private readonly DatabaseUpgradeService _databaseUpgradeService;
    private readonly SettingsRepository _settingsRepository;
    private readonly ViewerDocumentLoader _documentLoader;
    private readonly ManagedChessRules _chessRules;
    private readonly IMoveSoundService _moveSoundService;
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _settingsDebounce;
    private CancellationTokenSource? _loadRun;
    private CancellationTokenSource? _rulesRun;
    private CancellationTokenSource? _legalRun;
    private long _lastRenderMetricTimestamp;
    private AppSettings _settings = new();
    private BoardSkin _selectedSkin = BoardSkin.Chessmentor;
    private BoardOrientation _orientation = BoardOrientation.White;
    private bool _showCoordinates = true;
    private bool _headerCollapsed;
    private bool _gamesPanelCollapsed;
    private bool _moveSoundEnabled = true;
    private bool _isBusy;
    private double _gamesPanelWidth = 280;
    private double _movesPanelWidth = 340;
    private int _commentFontSize = 14;
    private FontFamily _commentFontFamily = CommentFontService.Resolve(CommentFontService.DefaultFamilyName, null);
    private ViewerMoveDisplayMode _moveDisplayMode = ViewerMoveDisplayMode.All;
    private ViewerNotationMode _notationMode = ViewerNotationMode.Letters;
    private ViewerMoveItem? _selectedMoveItem;
    private ViewerMoveRow? _selectedMoveRow;
    private ViewerBranchItem? _selectedBranch;
    private bool _disposed;
    private BoardOverlay _boardOverlay = new();
    private IReadOnlyList<LegalMove> _legalMoves = Array.Empty<LegalMove>();
    private string _status = "در حال آماده‌سازی Viewer 1…";

    public MainWindowViewModel(
        AppDatabase database,
        DatabaseUpgradeService databaseUpgradeService,
        SettingsRepository settingsRepository,
        ViewerDocumentLoader documentLoader,
        ManagedChessRules chessRules,
        IMoveSoundService moveSoundService)
    {
        _database = database;
        _databaseUpgradeService = databaseUpgradeService;
        _settingsRepository = settingsRepository;
        _documentLoader = documentLoader;
        _chessRules = chessRules;
        _moveSoundService = moveSoundService;
        _settingsRepository.Updated += OnSettingsUpdated;

        Session.PropertyChanged += OnSessionPropertyChanged;
        Session.Games.CollectionChanged += OnGamesCollectionChanged;
        Session.WorkspaceChanged += OnWorkspaceChanged;

        FlipBoardCommand = new RelayCommand(FlipBoard);
        PreviousGameCommand = new RelayCommand(() => NavigateGame(-1), () => Session.CanPreviousGame);
        PreviousMoveCommand = new RelayCommand(PreviousMove, () => Session.CanPreviousMove);
        MainlineCommand = new RelayCommand(SelectMainline, () => Session.CurrentNode?.Parent is not null);
        NextMoveCommand = new RelayCommand(NextMove, () => Session.CanNextMove);
        NextGameCommand = new RelayCommand(() => NavigateGame(1), () => Session.CanNextGame);
        ClearCommand = new RelayCommand(Clear, () => Session.HasGames && !IsBusy);
        ToggleHeaderCommand = new RelayCommand(() => HeaderCollapsed = !HeaderCollapsed);
        ToggleGamesPanelCommand = new RelayCommand(() => GamesPanelCollapsed = !GamesPanelCollapsed);
        ToggleSoundCommand = new RelayCommand(() => MoveSoundEnabled = !MoveSoundEnabled);
        ChooseBranchCommand = new RelayCommand(ChooseSelectedBranch, () => SelectedBranch is not null);
        CloseBranchChooserCommand = new RelayCommand(Session.CloseBranchChooser);
    }

    public ViewerSession Session { get; } = new();
    public ObservableCollection<ViewerGameItem> Games => Session.Games;
    public ObservableCollection<ViewerBranchItem> Branches => Session.Branches;
    public IReadOnlyList<BoardSkin> Skins { get; } = Enum.GetValues<BoardSkin>();
    public IReadOnlyList<int> CommentFontSizes => AllowedCommentFontSizes;
    public IReadOnlyList<CommentFontOption> CommentFontOptions => CommentFontService.BuiltInOptions;
    public IReadOnlyList<ViewerOption<ViewerMoveDisplayMode>> MoveDisplayModeOptions { get; } =
    [
        new(ViewerMoveDisplayMode.All, "همه حرکات"),
        new(ViewerMoveDisplayMode.Training, "حالت تمرین"),
        new(ViewerMoveDisplayMode.Mobile, "حالت موبایل"),
    ];

    public IReadOnlyList<ViewerOption<ViewerNotationMode>> NotationModeOptions { get; } =
    [
        new(ViewerNotationMode.Letters, "حروف لاتین (K Q R B N)"),
        new(ViewerNotationMode.Figurines, "نمادهای شطرنجی (♔ ♕ ♖ ♗ ♘)"),
    ];

    public PerformanceSnapshot Metrics { get; } = new();
    public RelayCommand FlipBoardCommand { get; }
    public RelayCommand PreviousGameCommand { get; }
    public RelayCommand PreviousMoveCommand { get; }
    public RelayCommand MainlineCommand { get; }
    public RelayCommand NextMoveCommand { get; }
    public RelayCommand NextGameCommand { get; }
    public RelayCommand ClearCommand { get; }
    public RelayCommand ToggleHeaderCommand { get; }
    public RelayCommand ToggleGamesPanelCommand { get; }
    public RelayCommand ToggleSoundCommand { get; }
    public RelayCommand ChooseBranchCommand { get; }
    public RelayCommand CloseBranchChooserCommand { get; }

    public ViewerGameItem? SelectedGame
    {
        get => Session.ActiveGame;
        set
        {
            if (value is not null && !ReferenceEquals(value, Session.ActiveGame))
            {
                Session.SelectGame(value);
                Status = $"بازی {value.FullTitle} انتخاب شد.";
            }
        }
    }

    public IReadOnlyList<ViewerMoveItem> MoveItems => Session.ActiveGame?.MoveItems ?? Array.Empty<ViewerMoveItem>();
    public IReadOnlyList<ViewerMoveRow> MoveRows => Session.ActiveGame?.MoveRows ?? Array.Empty<ViewerMoveRow>();
    public IReadOnlyList<PgnHeader> GameHeaders => Session.ActiveGame?.Headers ?? Array.Empty<PgnHeader>();
    public string RootComment => Session.ActiveGame?.RootComment ?? string.Empty;
    public bool HasRootComment => !string.IsNullOrWhiteSpace(RootComment);

    public ViewerMoveItem? SelectedMoveItem
    {
        get => _selectedMoveItem;
        set
        {
            if (!SetProperty(ref _selectedMoveItem, value) || value is null ||
                ReferenceEquals(value.Node, Session.CurrentNode))
            {
                return;
            }

            NavigateToNode(value.NodeId);
        }
    }

    public ViewerMoveRow? SelectedMoveRow
    {
        get => _selectedMoveRow;
        set => SetProperty(ref _selectedMoveRow, value);
    }

    public ViewerBranchItem? SelectedBranch
    {
        get => _selectedBranch;
        set
        {
            if (SetProperty(ref _selectedBranch, value))
            {
                ChooseBranchCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public BoardSkin SelectedSkin
    {
        get => _selectedSkin;
        set
        {
            if (SetProperty(ref _selectedSkin, value))
            {
                ScheduleSettingsSave();
            }
        }
    }

    public BoardOrientation Orientation
    {
        get => _orientation;
        private set
        {
            if (!SetProperty(ref _orientation, value))
            {
                return;
            }

            RaisePlayerProperties();
        }
    }

    public bool ShowCoordinates
    {
        get => _showCoordinates;
        set
        {
            if (SetProperty(ref _showCoordinates, value))
            {
                ScheduleSettingsSave();
            }
        }
    }

    public bool HeaderCollapsed
    {
        get => _headerCollapsed;
        set
        {
            if (SetProperty(ref _headerCollapsed, value))
            {
                ScheduleSettingsSave();
            }
        }
    }

    public bool GamesPanelCollapsed
    {
        get => _gamesPanelCollapsed;
        set
        {
            if (!SetProperty(ref _gamesPanelCollapsed, value))
            {
                return;
            }

            OnPropertyChanged(nameof(GamesColumnWidth));
            OnPropertyChanged(nameof(GamesSplitterWidth));
            ScheduleSettingsSave();
        }
    }

    public bool MoveSoundEnabled
    {
        get => _moveSoundEnabled;
        set
        {
            if (!SetProperty(ref _moveSoundEnabled, value))
            {
                return;
            }

            if (!value)
            {
                _moveSoundService.Stop();
            }

            OnPropertyChanged(nameof(SoundButtonLabel));
            ScheduleSettingsSave();
        }
    }

    public string SoundButtonLabel => MoveSoundEnabled ? "🔊" : "🔇";

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetProperty(ref _isBusy, value))
            {
                return;
            }

            OnPropertyChanged(nameof(CanOpenFiles));
            ClearCommand.RaiseCanExecuteChanged();
        }
    }

    public bool CanOpenFiles => !IsBusy;

    public double GamesPanelWidth
    {
        get => _gamesPanelWidth;
        private set => SetProperty(ref _gamesPanelWidth, Math.Clamp(value, 210, 520));
    }

    public double MovesPanelWidth
    {
        get => _movesPanelWidth;
        private set => SetProperty(ref _movesPanelWidth, Math.Clamp(value, 260, 620));
    }

    public GridLength GamesColumnWidth => new(GamesPanelCollapsed ? 52 : GamesPanelWidth);
    public GridLength GamesSplitterWidth => new(GamesPanelCollapsed ? 0 : 8);
    public GridLength MovesColumnWidth => new(MovesPanelWidth);

    public int CommentFontSize
    {
        get => _commentFontSize;
        set
        {
            var size = AllowedCommentFontSizes.Contains(value) ? value : 14;
            if (SetProperty(ref _commentFontSize, size))
            {
                ScheduleSettingsSave();
            }
        }
    }

    public FontFamily CommentFontFamily => _commentFontFamily;
    public string? SelectedBuiltInCommentFontFamily
    {
        get => string.IsNullOrWhiteSpace(_settings.CustomCommentFontPath) &&
               CommentFontService.IsBuiltIn(_settings.CommentFontFamilyName)
            ? _settings.CommentFontFamilyName
            : null;
        set
        {
            if (string.IsNullOrWhiteSpace(value) ||
                !CommentFontService.IsBuiltIn(value) ||
                string.Equals(SelectedBuiltInCommentFontFamily, value, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _settings = _settings with
            {
                CommentFontFamilyName = value,
                CustomCommentFontPath = string.Empty,
            };
            _commentFontFamily = CommentFontService.Resolve(value, null);
            OnPropertyChanged();
            OnPropertyChanged(nameof(CommentFontFamily));
            OnPropertyChanged(nameof(CommentFontLabel));
            ScheduleSettingsSave();
        }
    }

    public string CommentFontLabel => string.IsNullOrWhiteSpace(_settings.CustomCommentFontPath)
        ? _settings.CommentFontFamilyName
        : $"{_settings.CommentFontFamilyName} · شخصی";

    public async Task SelectCustomCommentFontAsync(
        string filePath,
        CancellationToken cancellationToken = default)
    {
        var selection = await CommentFontService.InstallAsync(filePath, cancellationToken).ConfigureAwait(true);
        _settings = await _settingsRepository.UpdateAsync(
            current => current with
            {
                CommentFontFamilyName = selection.FamilyName,
                CustomCommentFontPath = selection.FilePath,
            },
            cancellationToken).ConfigureAwait(true);
        Status = $"فونت توضیحات «{selection.FamilyName}» فعال شد.";
    }

    public async Task ResetCommentFontAsync(CancellationToken cancellationToken = default)
    {
        _settings = await _settingsRepository.UpdateAsync(
            current => current with
            {
                CommentFontFamilyName = CommentFontService.DefaultFamilyName,
                CustomCommentFontPath = string.Empty,
            },
            cancellationToken).ConfigureAwait(true);
        Status = "فونت توضیحات به ایران یکان بازگشت.";
    }

    public async Task<DatabaseUpgradeResult> UpgradeDatabaseAsync(
        string sourcePath,
        CancellationToken cancellationToken = default)
    {
        if (IsBusy)
        {
            throw new InvalidOperationException("برنامه در حال انجام عملیات دیگری است.");
        }

        IsBusy = true;
        Status = "در حال بررسی و ادغام دیتابیس انتخاب‌شده…";
        try
        {
            var result = await _databaseUpgradeService.ImportAsync(
                sourcePath,
                cancellationToken).ConfigureAwait(true);
            _settings = await _settingsRepository.ReloadAsync(cancellationToken).ConfigureAwait(true);
            Status = $"دیتابیس سازگار بود؛ {result.ImportedOrUpdatedRows:N0} رکورد وارد یا به‌روز شد.";
            return result;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Status = exception.Message;
            throw;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public ViewerOption<ViewerMoveDisplayMode> SelectedMoveDisplayMode
    {
        get => MoveDisplayModeOptions.First(option => option.Value == _moveDisplayMode);
        set
        {
            if (value is null || _moveDisplayMode == value.Value)
            {
                return;
            }

            _moveDisplayMode = value.Value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsAllMovesMode));
            OnPropertyChanged(nameof(IsTrainingMode));
            OnPropertyChanged(nameof(IsMobileMode));
            ScheduleSettingsSave();
        }
    }

    public ViewerOption<ViewerNotationMode> SelectedNotationMode
    {
        get => NotationModeOptions.First(option => option.Value == _notationMode);
        set
        {
            if (value is null || _notationMode == value.Value)
            {
                return;
            }

            _notationMode = value.Value;
            ApplyNotationMode();
            OnPropertyChanged();
            OnPropertyChanged(nameof(CurrentMoveLabel));
            ScheduleSettingsSave();
        }
    }

    public bool IsAllMovesMode => _moveDisplayMode == ViewerMoveDisplayMode.All;
    public bool IsTrainingMode => _moveDisplayMode == ViewerMoveDisplayMode.Training;
    public bool IsMobileMode => _moveDisplayMode == ViewerMoveDisplayMode.Mobile;
    public bool IsBranchChooserOpen => Session.IsBranchChooserOpen;
    public string BoardFen => Session.CurrentFen;
    public BoardOverlay BoardOverlay
    {
        get => _boardOverlay;
        private set => SetProperty(ref _boardOverlay, value);
    }

    public IReadOnlyList<LegalMove> LegalMoves
    {
        get => _legalMoves;
        private set => SetProperty(ref _legalMoves, value);
    }

    public string ActiveStartingComment => Session.CurrentNode is { IsRoot: false } node
        ? ViewerText.NormalizeCommentForDisplay(string.Join(Environment.NewLine, node.StartingComments.Select(static comment => comment.Text)))
        : string.Empty;
    public string ActiveComment => ViewerText.NormalizeCommentForDisplay(
        string.Join(
            Environment.NewLine,
            (Session.CurrentNode ?? Session.ActiveGame?.Game.Root)?.Comments.Select(static comment => comment.Text) ?? Enumerable.Empty<string>()));
    public bool HasActiveComment => !string.IsNullOrWhiteSpace(ActiveStartingComment) || !string.IsNullOrWhiteSpace(ActiveComment);
    public string CurrentMoveLabel => Session.CurrentNode is not { IsRoot: false } node
        ? "شروع بازی"
        : $"{node.FullmoveNumber ?? Math.Max(1, (node.Ply + 1) / 2)}{((node.IsWhiteMove ?? (node.Ply % 2 == 1)) ? "." : "...")} {ViewerNotation.FormatSan(node.RawSan, node.IsWhiteMove ?? (node.Ply % 2 == 1), _notationMode)}";
    public string TopPlayerName => Orientation == BoardOrientation.White ? BlackPlayer : WhitePlayer;
    public string BottomPlayerName => Orientation == BoardOrientation.White ? WhitePlayer : BlackPlayer;
    public string TopPlayerElo => Orientation == BoardOrientation.White ? BlackElo : WhiteElo;
    public string BottomPlayerElo => Orientation == BoardOrientation.White ? WhiteElo : BlackElo;
    public bool IsTopSideToMove => Session.ActiveGame is not null && (Orientation == BoardOrientation.White ? !WhiteToMove : WhiteToMove);
    public bool IsBottomSideToMove => Session.ActiveGame is not null && !IsTopSideToMove;
    public string CurrentFileName
    {
        get
        {
            var names = Games.Select(static game => game.SourceFileName).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            return names.Length switch
            {
                0 => "فایلی باز نیست",
                1 => names[0],
                _ => $"{names[0]} +{names.Length - 1}",
            };
        }
    }

    public int GameCount => Games.Count;
    public bool HasGames => Games.Count > 0;

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public double MemoryMegabytes => Metrics.ManagedMemoryBytes / 1024d / 1024d;

    public async Task InitializeAsync()
    {
        try
        {
            var stopwatch = Stopwatch.StartNew();
            await _database.InitializeAsync(_lifetime.Token).ConfigureAwait(true);
            var settings = await _settingsRepository.LoadAsync(_lifetime.Token).ConfigureAwait(true);
            stopwatch.Stop();
            Metrics.DatabaseMilliseconds = stopwatch.Elapsed.TotalMilliseconds;
            ApplySettings(settings);

            try
            {
                await _moveSoundService.InitializeAsync(_lifetime.Token).ConfigureAwait(true);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                MoveSoundEnabled = false;
                Status = $"Viewer 1 آماده است؛ راه‌اندازی صدا ناموفق بود: {exception.Message}";
                return;
            }

            Metrics.RefreshMemory();
            OnPropertyChanged(nameof(MemoryMegabytes));
            Status = "Viewer 1 آماده است؛ یک یا چند فایل PGN باز کنید.";
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Status = $"راه‌اندازی کامل نشد: {exception.Message}";
        }
    }

    public async Task LoadFilesAsync(IReadOnlyList<string> paths, bool append)
    {
        if (paths.Count == 0)
        {
            return;
        }

        _loadRun?.Cancel();
        _loadRun?.Dispose();
        var run = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _loadRun = run;
        IsBusy = true;
        Status = append ? "در حال افزودن PGN در پس‌زمینه…" : "در حال خواندن PGN در پس‌زمینه…";
        try
        {
            var batch = await _documentLoader.LoadAsync(paths, run.Token).ConfigureAwait(true);
            if (batch.Sources.Count == 0)
            {
                Status = batch.Diagnostics.FirstOrDefault() ?? "هیچ بازی معتبری پیدا نشد.";
                return;
            }

            if (append && Session.HasGames)
            {
                Session.Append(batch.Sources);
            }
            else
            {
                Session.Replace(batch.Sources);
            }

            ApplyNotationMode();
            Metrics.PgnParseMilliseconds = batch.ParseMilliseconds;
            Metrics.PgnSemanticMilliseconds = batch.SemanticMilliseconds;
            RefreshWorkspaceMetrics();
            var diagnosticText = batch.Diagnostics.Count == 0
                ? string.Empty
                : $"؛ {batch.Diagnostics.Count} هشدار ثبت شد";
            Status = append
                ? $"{batch.GameCount} بازی افزوده شد؛ مجموع {Games.Count} بازی{diagnosticText}."
                : $"{batch.GameCount} بازی و {batch.NodeCount} گره بارگذاری شد{diagnosticText}.";
        }
        catch (OperationCanceledException) when (run.IsCancellationRequested)
        {
            Status = "بارگذاری لغو شد.";
        }
        catch (Exception exception)
        {
            Status = $"خواندن PGN ناموفق بود: {exception.Message}";
        }
        finally
        {
            if (ReferenceEquals(_loadRun, run))
            {
                IsBusy = false;
            }
        }
    }

    public async Task HandleCandidateMoveAsync(BoardMoveRequestedEventArgs requested)
    {
        var startingNode = Session.CurrentNode;
        if (startingNode is null)
        {
            return;
        }

        _rulesRun?.Cancel();
        _rulesRun?.Dispose();
        var run = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _rulesRun = run;
        var currentNodeId = startingNode.StableId;
        var fen = BoardFen;
        try
        {
            var legalMoves = await Task.Run(
                () => _chessRules.GetLegalMoves(fen, run.Token),
                run.Token).ConfigureAwait(true);
            var currentNode = Session.CurrentNode;
            if (currentNode is null ||
                !string.Equals(currentNode.StableId, currentNodeId, StringComparison.Ordinal))
            {
                return;
            }

            var legal = legalMoves.FirstOrDefault(move =>
                move.From == requested.From &&
                move.To == requested.To &&
                (requested.Promotion is null || move.Promotion == char.ToLowerInvariant(requested.Promotion.Value)));
            if (legal is null)
            {
                Status = "این حرکت در موقعیت فعلی قانونی نیست.";
                return;
            }

            var child = currentNode.Children.FirstOrDefault(node =>
                string.Equals(node.Uci, legal.Uci, StringComparison.Ordinal));
            if (child is null)
            {
                Status = "در Viewer 1 فقط ادامه‌های موجود در PGN قابل انتخاب‌اند؛ افزودن شاخه در Studio انجام می‌شود.";
                return;
            }

            Session.SelectNode(child.StableId);
            PlayNavigationSound(currentNode, child);
            Status = $"{legal.San} انتخاب شد.";
        }
        catch (OperationCanceledException) when (run.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Status = $"اعتبارسنجی حرکت ناموفق بود: {exception.Message}";
        }
    }

    public void RemoveGame(ViewerGameItem game)
    {
        if (Session.Remove(game))
        {
            RefreshWorkspaceMetrics();
            Status = $"بازی حذف شد؛ {Games.Count} بازی باقی ماند.";
        }
    }

    public void RemoveMarkedGames()
    {
        var count = Session.RemoveMarked();
        if (count > 0)
        {
            RefreshWorkspaceMetrics();
            Status = $"{count} بازی انتخاب‌شده حذف شد؛ {Games.Count} بازی باقی ماند.";
        }
    }

    public void MarkAllGames(bool marked)
    {
        foreach (var game in Games)
        {
            game.IsMarked = marked;
        }
    }

    public void UpdatePanelWidths(double movesWidth, double gamesWidth)
    {
        MovesPanelWidth = movesWidth;
        GamesPanelWidth = gamesWidth;
        OnPropertyChanged(nameof(MovesColumnWidth));
        OnPropertyChanged(nameof(GamesColumnWidth));
        ScheduleSettingsSave();
    }

    public void HandleKeyboardNavigation(System.Windows.Input.Key key)
    {
        switch (key)
        {
            case System.Windows.Input.Key.Left:
                PreviousMove();
                break;
            case System.Windows.Input.Key.Right:
                NextMove();
                break;
            case System.Windows.Input.Key.Home:
                NavigateGame(-1);
                break;
            case System.Windows.Input.Key.End:
                NavigateGame(1);
                break;
            case System.Windows.Input.Key.Escape:
                Session.CloseBranchChooser();
                break;
        }
    }

    public void RecordRender(double milliseconds)
    {
        var now = Stopwatch.GetTimestamp();
        if (_lastRenderMetricTimestamp != 0 &&
            Stopwatch.GetElapsedTime(_lastRenderMetricTimestamp, now) < TimeSpan.FromMilliseconds(250))
        {
            return;
        }

        _lastRenderMetricTimestamp = now;
        Metrics.RenderMilliseconds = milliseconds;
        Metrics.RefreshMemory();
        OnPropertyChanged(nameof(MemoryMegabytes));
    }

    public void ReportPositionError(string message) => Status = $"FEN نامعتبر: {message}";

    private string WhitePlayer => Session.ActiveGame?.White ?? "سفید";
    private string BlackPlayer => Session.ActiveGame?.Black ?? "سیاه";
    private string WhiteElo => Session.ActiveGame?.Game.Header("WhiteElo") is { Length: > 0 } value ? value : string.Empty;
    private string BlackElo => Session.ActiveGame?.Game.Header("BlackElo") is { Length: > 0 } value ? value : string.Empty;
    private bool WhiteToMove
    {
        get
        {
            var fields = BoardFen.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return fields.Length < 2 || fields[1] == "w";
        }
    }

    private void FlipBoard()
    {
        Orientation = Orientation == BoardOrientation.White ? BoardOrientation.Black : BoardOrientation.White;
        Status = Orientation == BoardOrientation.White ? "سفید پایین برد است." : "سیاه پایین برد است.";
    }

    private void PreviousMove()
    {
        var previous = Session.CurrentNode;
        if (Session.PreviousMove() && previous is not null && Session.CurrentNode is not null)
        {
            PlayNavigationSound(previous, Session.CurrentNode);
        }
    }

    private void NextMove()
    {
        var previous = Session.CurrentNode;
        var result = Session.NextMove();
        if (result == ViewerNavigationResult.Moved && previous is not null && Session.CurrentNode is not null)
        {
            PlayNavigationSound(previous, Session.CurrentNode);
        }
        else if (result == ViewerNavigationResult.BranchSelectionRequired)
        {
            SelectedBranch = Branches.FirstOrDefault();
            Status = "ادامهٔ دلخواه را از انتخاب‌گر شاخه تعیین کنید.";
        }
    }

    private void NavigateToNode(string nodeId)
    {
        var previous = Session.CurrentNode;
        if (Session.SelectNode(nodeId) && previous is not null && Session.CurrentNode is not null)
        {
            PlayNavigationSound(previous, Session.CurrentNode);
        }
    }

    private void SelectMainline()
    {
        var previous = Session.CurrentNode;
        if (Session.SelectMainlineSibling() && previous is not null && Session.CurrentNode is not null)
        {
            PlayNavigationSound(previous, Session.CurrentNode);
        }
    }

    private void ChooseSelectedBranch()
    {
        if (SelectedBranch is null)
        {
            return;
        }

        var previous = Session.CurrentNode;
        if (Session.SelectBranch(SelectedBranch.Index) && previous is not null && Session.CurrentNode is not null)
        {
            PlayNavigationSound(previous, Session.CurrentNode);
        }
    }

    private void NavigateGame(int offset)
    {
        if (Session.SelectGameByOffset(offset))
        {
            Status = $"بازی {Session.ActiveGame?.FullTitle} انتخاب شد.";
        }
    }

    private void PlayNavigationSound(PgnMoveNode previous, PgnMoveNode current)
    {
        if (!MoveSoundEnabled || ReferenceEquals(previous, current))
        {
            return;
        }

        var soundedNode = current.Ply < previous.Ply ? previous : current;
        if (!soundedNode.IsRoot)
        {
            _moveSoundService.Play(MoveSoundClassifier.FromSan(soundedNode.RawSan));
        }
    }

    private void Clear()
    {
        _loadRun?.Cancel();
        Session.Clear();
        Metrics.PgnParseMilliseconds = 0;
        Metrics.PgnSemanticMilliseconds = 0;
        RefreshWorkspaceMetrics();
        Status = "Workspace پاک شد؛ یک یا چند فایل PGN باز کنید.";
    }

    private void ApplySettings(AppSettings settings)
    {
        _settings = settings;
        _selectedSkin = settings.BoardSkin;
        _showCoordinates = settings.ShowCoordinates;
        _headerCollapsed = settings.HeaderCollapsed;
        _gamesPanelCollapsed = settings.GamesPanelCollapsed;
        _moveSoundEnabled = settings.MoveSoundEnabled;
        _gamesPanelWidth = Math.Clamp(settings.GamesPanelWidth, 210, 520);
        _movesPanelWidth = Math.Clamp(settings.MovesPanelWidth, 260, 620);
        _commentFontSize = AllowedCommentFontSizes.Contains(settings.CommentFontSize) ? settings.CommentFontSize : 14;
        _commentFontFamily = CommentFontService.Resolve(
            settings.CommentFontFamilyName,
            settings.CustomCommentFontPath);
        _moveDisplayMode = Enum.TryParse<ViewerMoveDisplayMode>(settings.ViewerMoveDisplayMode, true, out var displayMode)
            ? displayMode
            : ViewerMoveDisplayMode.All;
        _notationMode = Enum.TryParse<ViewerNotationMode>(settings.ViewerNotationMode, true, out var notationMode)
            ? notationMode
            : ViewerNotationMode.Letters;

        foreach (var property in new[]
                 {
                     nameof(SelectedSkin), nameof(ShowCoordinates), nameof(HeaderCollapsed),
                     nameof(GamesPanelCollapsed), nameof(MoveSoundEnabled), nameof(SoundButtonLabel),
                     nameof(GamesColumnWidth), nameof(GamesSplitterWidth), nameof(MovesColumnWidth),
                     nameof(CommentFontSize), nameof(CommentFontFamily), nameof(CommentFontLabel),
                     nameof(SelectedBuiltInCommentFontFamily),
                     nameof(SelectedMoveDisplayMode), nameof(SelectedNotationMode),
                     nameof(IsAllMovesMode), nameof(IsTrainingMode), nameof(IsMobileMode),
                 })
        {
            OnPropertyChanged(property);
        }
    }

    private void OnSettingsUpdated(AppSettings settings)
    {
        ApplySettings(settings);
        ApplyNotationMode();
    }

    private void OnSessionPropertyChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        switch (eventArgs.PropertyName)
        {
            case nameof(ViewerSession.ActiveGame):
                OnPropertyChanged(nameof(SelectedGame));
                OnPropertyChanged(nameof(MoveItems));
                OnPropertyChanged(nameof(MoveRows));
                OnPropertyChanged(nameof(GameHeaders));
                OnPropertyChanged(nameof(RootComment));
                OnPropertyChanged(nameof(HasRootComment));
                OnPropertyChanged(nameof(CurrentFileName));
                RaisePlayerProperties();
                break;
            case nameof(ViewerSession.CurrentNode):
                OnPropertyChanged(nameof(BoardFen));
                UpdateBoardOverlay();
                LegalMoves = Array.Empty<LegalMove>();
                _ = RefreshLegalMovesAsync();
                OnPropertyChanged(nameof(ActiveStartingComment));
                OnPropertyChanged(nameof(ActiveComment));
                OnPropertyChanged(nameof(HasActiveComment));
                OnPropertyChanged(nameof(CurrentMoveLabel));
                RaisePlayerProperties();
                break;
            case nameof(ViewerSession.ActiveMoveItem):
                _selectedMoveItem = Session.ActiveMoveItem;
                _selectedMoveRow = Session.ActiveMoveItem is null
                    ? null
                    : Session.ActiveGame?.FindMoveRow(Session.ActiveMoveItem.NodeId);
                OnPropertyChanged(nameof(SelectedMoveItem));
                OnPropertyChanged(nameof(SelectedMoveRow));
                break;
            case nameof(ViewerSession.IsBranchChooserOpen):
                OnPropertyChanged(nameof(IsBranchChooserOpen));
                if (!Session.IsBranchChooserOpen)
                {
                    SelectedBranch = null;
                }

                break;
        }

        RaiseCommandStates();
    }

    private void OnGamesChanged()
    {
        OnPropertyChanged(nameof(CurrentFileName));
        OnPropertyChanged(nameof(GameCount));
        OnPropertyChanged(nameof(HasGames));
        RefreshWorkspaceMetrics();
        RaiseCommandStates();
    }

    private void OnGamesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs eventArgs)
    {
        if (!Session.IsBulkUpdating)
        {
            OnGamesChanged();
        }
    }

    private void OnWorkspaceChanged(object? sender, EventArgs eventArgs) => OnGamesChanged();

    private void UpdateBoardOverlay()
    {
        var uci = Session.CurrentNode?.Uci;
        if (uci is { Length: >= 4 } &&
            Square.TryParse(uci[..2], out var from) &&
            Square.TryParse(uci.Substring(2, 2), out var to))
        {
            BoardOverlay = new BoardOverlay(LastMoveFrom: from, LastMoveTo: to);
        }
        else
        {
            BoardOverlay = new BoardOverlay();
        }
    }

    private async Task RefreshLegalMovesAsync()
    {
        _legalRun?.Cancel();
        _legalRun?.Dispose();
        if (Session.CurrentNode is null)
        {
            LegalMoves = Array.Empty<LegalMove>();
            return;
        }

        var run = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _legalRun = run;
        var nodeId = Session.CurrentNode.StableId;
        var fen = BoardFen;
        try
        {
            var moves = await Task.Run(
                () => _chessRules.GetLegalMoves(fen, run.Token),
                run.Token).ConfigureAwait(true);
            if (string.Equals(Session.CurrentNode?.StableId, nodeId, StringComparison.Ordinal))
            {
                LegalMoves = moves;
            }
        }
        catch (OperationCanceledException) when (run.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            LegalMoves = Array.Empty<LegalMove>();
            Status = $"محاسبهٔ حرکات قانونی ناموفق بود: {exception.Message}";
        }
    }

    private void RaisePlayerProperties()
    {
        OnPropertyChanged(nameof(TopPlayerName));
        OnPropertyChanged(nameof(BottomPlayerName));
        OnPropertyChanged(nameof(TopPlayerElo));
        OnPropertyChanged(nameof(BottomPlayerElo));
        OnPropertyChanged(nameof(IsTopSideToMove));
        OnPropertyChanged(nameof(IsBottomSideToMove));
    }

    private void ApplyNotationMode()
    {
        foreach (var move in Games.SelectMany(static game => game.MoveItems))
        {
            move.SetNotationMode(_notationMode);
        }
    }

    private void RefreshWorkspaceMetrics()
    {
        Metrics.GameCount = Games.Count;
        Metrics.NodeCount = Games.Sum(static game => game.Game.NodeCount);
        Metrics.RefreshMemory();
        OnPropertyChanged(nameof(MemoryMegabytes));
        OnPropertyChanged(nameof(CurrentFileName));
    }

    private void RaiseCommandStates()
    {
        PreviousGameCommand.RaiseCanExecuteChanged();
        PreviousMoveCommand.RaiseCanExecuteChanged();
        MainlineCommand.RaiseCanExecuteChanged();
        NextMoveCommand.RaiseCanExecuteChanged();
        NextGameCommand.RaiseCanExecuteChanged();
        ClearCommand.RaiseCanExecuteChanged();
    }

    private void ScheduleSettingsSave()
    {
        _settingsDebounce?.Cancel();
        _settingsDebounce?.Dispose();
        _settingsDebounce = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _ = SaveSettingsAfterDelayAsync(_settingsDebounce.Token);
    }

    private async Task SaveSettingsAfterDelayAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(400), cancellationToken).ConfigureAwait(true);
            _settings = await _settingsRepository.UpdateAsync(
                current => current with
                {
                    BoardSkin = SelectedSkin,
                    ShowCoordinates = ShowCoordinates,
                    HeaderCollapsed = HeaderCollapsed,
                    GamesPanelCollapsed = GamesPanelCollapsed,
                    ViewerMoveDisplayMode = _moveDisplayMode.ToString(),
                    ViewerNotationMode = _notationMode.ToString(),
                    MoveSoundEnabled = MoveSoundEnabled,
                    GamesPanelWidth = GamesPanelWidth,
                    MovesPanelWidth = MovesPanelWidth,
                    CommentFontSize = CommentFontSize,
                    CommentFontFamilyName = _settings.CommentFontFamilyName,
                    CustomCommentFontPath = _settings.CustomCommentFontPath,
                },
                cancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Status = $"ذخیره تنظیمات ناموفق بود: {exception.Message}";
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _settingsRepository.Updated -= OnSettingsUpdated;
        Session.PropertyChanged -= OnSessionPropertyChanged;
        Session.Games.CollectionChanged -= OnGamesCollectionChanged;
        Session.WorkspaceChanged -= OnWorkspaceChanged;
        _lifetime.Cancel();
        _settingsDebounce?.Cancel();
        _loadRun?.Cancel();
        _rulesRun?.Cancel();
        _legalRun?.Cancel();
        _settingsDebounce?.Dispose();
        _loadRun?.Dispose();
        _rulesRun?.Dispose();
        _legalRun?.Dispose();
        _moveSoundService.Dispose();
        _lifetime.Dispose();
    }
}

public sealed record ViewerOption<T>(T Value, string Label);
