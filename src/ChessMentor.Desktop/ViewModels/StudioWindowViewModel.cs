using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using ChessMentor.Audio;
using ChessMentor.Chess;
using ChessMentor.Core.Diagnostics;
using ChessMentor.Core.Mvvm;
using ChessMentor.Desktop.Services;
using ChessMentor.Persistence;
using ChessMentor.Pgn;
using ChessMentor.ServerClient;
using ChessMentor.Studio;
using ChessMentor.Translation;
using ChessMentor.Viewer;

namespace ChessMentor.Desktop.ViewModels;

public sealed class StudioWindowViewModel : ObservableObject, IDisposable
{
    private static readonly IReadOnlyList<int> AllowedCommentFontSizes =
        [12, 13, 14, 15, 16, 18, 20, 22, 24, 26, 28];
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
    };
    private readonly AppDatabase _database;
    private readonly SettingsRepository _settingsRepository;
    private readonly LocalDraftRepository _draftRepository;
    private readonly TranslationCacheRepository _translationCache;
    private readonly SyncQueueRepository _syncQueue;
    private readonly TranslationBacklog _translationBacklog;
    private readonly AudioMetadataRepository _audioMetadata;
    private readonly ViewerDocumentLoader _documentLoader;
    private readonly IChessRules _chessRules;
    private readonly IMoveSoundService _moveSound;
    private readonly IMoveAudioRecorder _audioRecorder;
    private readonly IMoveAudioPlayer _audioPlayer;
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _loadRun;
    private CancellationTokenSource? _legalRun;
    private CancellationTokenSource? _candidateRun;
    private CancellationTokenSource? _autosaveRun;
    private CancellationTokenSource? _settingsRun;
    private CancellationTokenSource? _audioRun;
    private CancellationTokenSource? _recordingClockRun;
    private ServerApiClient? _server;
    private string _connectedServerUrl = string.Empty;
    private string _localInstallationId = string.Empty;
    private string? _currentServerUserId;
    private AppSettings _settings = new();
    private ViewerGameItem? _selectedGame;
    private ViewerMoveItem? _selectedMoveItem;
    private ViewerMoveRow? _selectedMoveRow;
    private ViewerBranchItem? _selectedBranch;
    private IReadOnlyList<LegalMove> _legalMoves = Array.Empty<LegalMove>();
    private BoardOverlay _boardOverlay = new();
    private BoardOrientation _orientation = BoardOrientation.White;
    private BoardSkin _selectedSkin = BoardSkin.Chessmentor;
    private bool _showCoordinates = true;
    private bool _moveSoundEnabled = true;
    private bool _headerCollapsed;
    private int _commentFontSize = 14;
    private FontFamily _commentFontFamily = CommentFontService.Resolve(CommentFontService.DefaultFamilyName, null);
    private ViewerNotationMode _notationMode = ViewerNotationMode.Letters;
    private bool _isBusy;
    private bool _isConnected;
    private bool _isAuthenticated;
    private bool _isAdmin;
    private bool _translationInProgress;
    private string _status = "Studio در حال راه‌اندازی است…";
    private string _authStatus = "حالت آفلاین";
    private string _serverBaseUrl = "https://chessfa.liara.run/";
    private string _startingCommentEditor = string.Empty;
    private string _commentEditor = string.Empty;
    private string _draftTitle = "پیش‌نویس بدون عنوان";
    private string? _draftId;
    private long? _serverDraftId;
    private long? _serverCourseId;
    private string _serverDraftIdText = string.Empty;
    private string _categorySlug = "training";
    private string _publishSlug = string.Empty;
    private string _featuredImagePath = string.Empty;
    private string _featuredImageName = string.Empty;
    private int _creditPriceMinor;
    private int _translationConcurrency = 3;
    private int _translationTotal;
    private int _translationCompleted;
    private int _translationFailed;
    private int _translationPercentage;
    private int _syncQueueCount;
    private double _studioMovesPanelWidth = 390;
    private double _studioGamesPanelWidth = 300;
    private string _translationMessage = string.Empty;
    private LocalDraftRecord? _selectedLocalDraft;
    private long _lastRenderMetricTimestamp;
    private bool _metadataDirty;
    private bool _suppressAutosave;
    private bool _isRecording;
    private bool _isAudioPlaying;
    private bool _updatingAudioPosition;
    private long _audioPositionMilliseconds;
    private long _audioDurationMilliseconds;
    private string _recordingStatus = string.Empty;
    private StudioAudioItem? _selectedAudio;
    private string? _openedAudioId;
    private string? _serverAudioLoadedKey;
    private RecordingContext? _recordingContext;
    private string _translationCacheSearch = string.Empty;
    private string _translationCacheEditor = string.Empty;
    private TranslationCacheEntry? _selectedTranslationCacheEntry;
    private bool _disposed;

    public StudioWindowViewModel(
        AppDatabase database,
        SettingsRepository settingsRepository,
        ViewerDocumentLoader documentLoader,
        IChessRules chessRules,
        IMoveSoundService moveSound,
        IMoveAudioRecorder audioRecorder,
        IMoveAudioPlayer audioPlayer)
    {
        _database = database;
        _settingsRepository = settingsRepository;
        _draftRepository = new LocalDraftRepository(database);
        _translationCache = new TranslationCacheRepository(database);
        _syncQueue = new SyncQueueRepository(database);
        _translationBacklog = new TranslationBacklog(_syncQueue);
        _audioMetadata = new AudioMetadataRepository(database);
        _documentLoader = documentLoader;
        _chessRules = chessRules;
        _moveSound = moveSound;
        _audioRecorder = audioRecorder;
        _audioPlayer = audioPlayer;
        _settingsRepository.Updated += OnSettingsUpdated;
        _audioPlayer.StateChanged += OnAudioPlaybackStateChanged;
        Workspace = new StudioWorkspace();
        Workspace.Changed += OnWorkspaceChanged;
        Session.PropertyChanged += OnSessionPropertyChanged;
        Session.Games.CollectionChanged += OnGamesCollectionChanged;

        ToggleHeaderCommand = new RelayCommand(() => HeaderCollapsed = !HeaderCollapsed);
        FlipBoardCommand = new RelayCommand(FlipBoard);
        PreviousGameCommand = new RelayCommand(() => NavigateGame(-1), () => Session.CanPreviousGame);
        PreviousMoveCommand = new RelayCommand(PreviousMove, () => Session.CanPreviousMove);
        NextMoveCommand = new RelayCommand(NextMove, () => Session.CanNextMove);
        NextGameCommand = new RelayCommand(() => NavigateGame(1), () => Session.CanNextGame);
        ChooseBranchCommand = new RelayCommand(ChooseBranch, () => SelectedBranch is not null);
        ApplyCommentsCommand = new AsyncRelayCommand(ApplyCommentsCommandAsync, () => Session.CurrentNode is not null);
        PromoteBranchCommand = new RelayCommand(PromoteBranch, CanMutateCurrentBranch);
        SaveLocalDraftCommand = new AsyncRelayCommand(
            SaveLocalDraftCommandAsync,
            () => Session.HasGames && !_translationInProgress);
        ResumeLocalDraftCommand = new AsyncRelayCommand(
            ResumeSelectedDraftAsync,
            () => SelectedLocalDraft is not null && !_translationInProgress);
        TranslateCommand = new AsyncRelayCommand(TranslateAsync, () => Session.HasGames && !_translationInProgress);
        CancelTranslationCommand = new RelayCommand(() => TranslateCommand.Cancel(), () => _translationInProgress);
        ConnectCommand = new AsyncRelayCommand(ConnectAsync, () => !_translationInProgress);
        SaveServerDraftCommand = new AsyncRelayCommand(SaveServerDraftAsync, CanUseAdminServer);
        PublishCommand = new AsyncRelayCommand(PublishAsync, CanUseAdminServer);
        LoadServerDraftCommand = new AsyncRelayCommand(LoadServerDraftAsync, CanLoadServerDraft);
        RecordCourseAudioCommand = new AsyncRelayCommand(
            cancellationToken => StartRecordingAsync("course", cancellationToken),
            CanRecordCourseAudio);
        RecordUserAudioCommand = new AsyncRelayCommand(
            cancellationToken => StartRecordingAsync("user", cancellationToken),
            CanRecordUserAudio);
        StopRecordingCommand = new AsyncRelayCommand(StopRecordingAsync, () => IsRecording);
        PlayAudioCommand = new AsyncRelayCommand(PlaySelectedAudioAsync, () => SelectedAudio is not null && !IsRecording);
        DeleteAudioCommand = new AsyncRelayCommand(DeleteSelectedAudioAsync, () => CanDeleteSelectedAudio);
        SearchTranslationCacheCommand = new AsyncRelayCommand(SearchTranslationCacheAsync);
        ApplyTranslationCacheEditCommand = new AsyncRelayCommand(
            ApplyTranslationCacheEditAsync,
            () => SelectedTranslationCacheEntry is not null && !string.IsNullOrWhiteSpace(TranslationCacheEditor));
    }

    public StudioWorkspace Workspace { get; }
    public ViewerSession Session => Workspace.Session;
    public ObservableCollection<ViewerGameItem> Games => Session.Games;
    public ObservableCollection<ViewerBranchItem> Branches => Session.Branches;
    public ObservableCollection<LocalDraftRecord> LocalDrafts { get; } = new();
    public ObservableCollection<StudioCategory> Categories { get; } = new();
    public ObservableCollection<StudioAudioItem> AudioItems { get; } = new();
    public ObservableCollection<TranslationCacheEntry> TranslationCacheRows { get; } = new();
    public IReadOnlyList<BoardSkin> Skins { get; } = Enum.GetValues<BoardSkin>();
    public IReadOnlyList<int> CommentFontSizes => AllowedCommentFontSizes;
    public IReadOnlyList<CommentFontOption> CommentFontOptions => CommentFontService.BuiltInOptions;
    public IReadOnlyList<int> TranslationConcurrencyOptions { get; } = Enumerable.Range(1, 12).ToArray();
    public IReadOnlyList<ViewerOption<ViewerNotationMode>> NotationModeOptions { get; } =
    [
        new(ViewerNotationMode.Letters, "حروف لاتین (K Q R B N)"),
        new(ViewerNotationMode.Figurines, "نمادهای شطرنجی (♔ ♕ ♖ ♗ ♘)"),
    ];
    public PerformanceSnapshot Metrics { get; } = new();

    public RelayCommand ToggleHeaderCommand { get; }
    public RelayCommand FlipBoardCommand { get; }
    public RelayCommand PreviousGameCommand { get; }
    public RelayCommand PreviousMoveCommand { get; }
    public RelayCommand NextMoveCommand { get; }
    public RelayCommand NextGameCommand { get; }
    public RelayCommand ChooseBranchCommand { get; }
    public AsyncRelayCommand ApplyCommentsCommand { get; }
    public RelayCommand PromoteBranchCommand { get; }
    public AsyncRelayCommand SaveLocalDraftCommand { get; }
    public AsyncRelayCommand ResumeLocalDraftCommand { get; }
    public AsyncRelayCommand TranslateCommand { get; }
    public RelayCommand CancelTranslationCommand { get; }
    public AsyncRelayCommand ConnectCommand { get; }
    public AsyncRelayCommand SaveServerDraftCommand { get; }
    public AsyncRelayCommand PublishCommand { get; }
    public AsyncRelayCommand LoadServerDraftCommand { get; }
    public AsyncRelayCommand RecordCourseAudioCommand { get; }
    public AsyncRelayCommand RecordUserAudioCommand { get; }
    public AsyncRelayCommand StopRecordingCommand { get; }
    public AsyncRelayCommand PlayAudioCommand { get; }
    public AsyncRelayCommand DeleteAudioCommand { get; }
    public AsyncRelayCommand SearchTranslationCacheCommand { get; }
    public AsyncRelayCommand ApplyTranslationCacheEditCommand { get; }

    public IReadOnlyList<ViewerMoveItem> MoveItems =>
        Session.ActiveGame?.MoveItems ?? Array.Empty<ViewerMoveItem>();

    public IReadOnlyList<ViewerMoveRow> MoveRows =>
        Session.ActiveGame?.MoveRows ?? Array.Empty<ViewerMoveRow>();

    public IReadOnlyList<PgnHeader> GameHeaders =>
        Session.ActiveGame?.Headers ?? Array.Empty<PgnHeader>();

    public ViewerGameItem? SelectedGame
    {
        get => _selectedGame;
        set
        {
            if (!SetProperty(ref _selectedGame, value) || value is null || ReferenceEquals(value, Session.ActiveGame))
            {
                return;
            }

            Session.SelectGame(value);
        }
    }

    public ViewerMoveItem? SelectedMoveItem
    {
        get => _selectedMoveItem;
        set
        {
            if (!SetProperty(ref _selectedMoveItem, value) || value is null)
            {
                return;
            }

            var previous = Session.CurrentNode;
            if (Session.SelectNode(value.NodeId) && previous is not null && Session.CurrentNode is not null)
            {
                PlayNavigationSound(previous, Session.CurrentNode);
            }
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

    public LocalDraftRecord? SelectedLocalDraft
    {
        get => _selectedLocalDraft;
        set
        {
            if (SetProperty(ref _selectedLocalDraft, value))
            {
                ResumeLocalDraftCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string BoardFen => Session.CurrentFen;

    public IReadOnlyList<LegalMove> LegalMoves
    {
        get => _legalMoves;
        private set => SetProperty(ref _legalMoves, value);
    }

    public BoardOverlay BoardOverlay
    {
        get => _boardOverlay;
        private set => SetProperty(ref _boardOverlay, value);
    }

    public BoardOrientation Orientation
    {
        get => _orientation;
        private set
        {
            if (SetProperty(ref _orientation, value))
            {
                OnPropertyChanged(nameof(TopPlayerName));
                OnPropertyChanged(nameof(BottomPlayerName));
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
                _moveSound.Stop();
            }

            ScheduleSettingsSave();
        }
    }

    public int CommentFontSize
    {
        get => _commentFontSize;
        set
        {
            var safe = AllowedCommentFontSizes.Contains(value) ? value : 14;
            if (SetProperty(ref _commentFontSize, safe))
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

    public ViewerOption<ViewerNotationMode> SelectedNotationMode
    {
        get => NotationModeOptions.First(option => option.Value == _notationMode);
        set
        {
            if (value is null || value.Value == _notationMode)
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

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                RaiseCommandStates();
            }
        }
    }

    public bool IsConnected
    {
        get => _isConnected;
        private set => SetProperty(ref _isConnected, value);
    }

    public bool IsAuthenticated
    {
        get => _isAuthenticated;
        private set => SetProperty(ref _isAuthenticated, value);
    }

    public bool IsAdmin
    {
        get => _isAdmin;
        private set
        {
            if (SetProperty(ref _isAdmin, value))
            {
                RaiseCommandStates();
            }
        }
    }

    public bool TranslationInProgress
    {
        get => _translationInProgress;
        private set
        {
            if (SetProperty(ref _translationInProgress, value))
            {
                RaiseCommandStates();
            }
        }
    }

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public string AuthStatus
    {
        get => _authStatus;
        private set => SetProperty(ref _authStatus, value);
    }

    public string ServerBaseUrl
    {
        get => _serverBaseUrl;
        set
        {
            if (SetProperty(ref _serverBaseUrl, value))
            {
                IsConnected = false;
                IsAuthenticated = false;
                IsAdmin = false;
                _currentServerUserId = null;
                AuthStatus = "آدرس تغییر کرده؛ اتصال را بزنید.";
                ScheduleSettingsSave();
            }
        }
    }

    public string StartingCommentEditor
    {
        get => _startingCommentEditor;
        set => SetProperty(ref _startingCommentEditor, value);
    }

    public string CommentEditor
    {
        get => _commentEditor;
        set => SetProperty(ref _commentEditor, value);
    }

    public string DraftTitle
    {
        get => _draftTitle;
        set
        {
            if (SetProperty(ref _draftTitle, value))
            {
                OnPropertyChanged(nameof(CurrentDraftLabel));
                MarkMetadataChanged();
            }
        }
    }

    public string CurrentDraftLabel
    {
        get
        {
            var draftId = DraftId;
            return draftId is null
                ? "ذخیره‌نشده"
                : $"{DraftTitle} · {draftId[..Math.Min(18, draftId.Length)]}";
        }
    }

    public string? DraftId
    {
        get => _draftId;
        private set
        {
            if (SetProperty(ref _draftId, value))
            {
                OnPropertyChanged(nameof(CurrentDraftLabel));
            }
        }
    }

    public long? ServerDraftId
    {
        get => _serverDraftId;
        private set
        {
            if (SetProperty(ref _serverDraftId, value))
            {
                _serverAudioLoadedKey = null;
                ServerDraftIdText = value?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
            }
        }
    }

    public long? ServerCourseId
    {
        get => _serverCourseId;
        private set
        {
            if (SetProperty(ref _serverCourseId, value))
            {
                _serverAudioLoadedKey = null;
            }
        }
    }

    public string ServerDraftIdText
    {
        get => _serverDraftIdText;
        set => SetProperty(ref _serverDraftIdText, value);
    }

    public string CategorySlug
    {
        get => _categorySlug;
        set
        {
            if (SetProperty(ref _categorySlug, value))
            {
                MarkMetadataChanged();
            }
        }
    }

    public string PublishSlug
    {
        get => _publishSlug;
        set
        {
            if (SetProperty(ref _publishSlug, value))
            {
                MarkMetadataChanged();
            }
        }
    }

    public int CreditPriceMinor
    {
        get => _creditPriceMinor;
        set
        {
            if (SetProperty(ref _creditPriceMinor, Math.Max(0, value)))
            {
                MarkMetadataChanged();
            }
        }
    }

    public string FeaturedImagePath
    {
        get => _featuredImagePath;
        private set
        {
            if (SetProperty(ref _featuredImagePath, value))
            {
                OnPropertyChanged(nameof(HasFeaturedImage));
            }
        }
    }

    public string FeaturedImageName
    {
        get => _featuredImageName;
        private set => SetProperty(ref _featuredImageName, value);
    }

    public bool HasFeaturedImage => !string.IsNullOrWhiteSpace(FeaturedImagePath);

    public int TranslationConcurrency
    {
        get => _translationConcurrency;
        set
        {
            var safe = Math.Clamp(value, 1, 12);
            if (SetProperty(ref _translationConcurrency, safe))
            {
                ScheduleSettingsSave();
            }
        }
    }

    public int TranslationTotal
    {
        get => _translationTotal;
        private set => SetProperty(ref _translationTotal, value);
    }

    public int TranslationCompleted
    {
        get => _translationCompleted;
        private set => SetProperty(ref _translationCompleted, value);
    }

    public int TranslationFailed
    {
        get => _translationFailed;
        private set => SetProperty(ref _translationFailed, value);
    }

    public int TranslationPercentage
    {
        get => _translationPercentage;
        private set => SetProperty(ref _translationPercentage, value);
    }

    public string TranslationMessage
    {
        get => _translationMessage;
        private set => SetProperty(ref _translationMessage, value);
    }

    public int SyncQueueCount
    {
        get => _syncQueueCount;
        private set => SetProperty(ref _syncQueueCount, value);
    }

    public double StudioMovesPanelWidth
    {
        get => _studioMovesPanelWidth;
        private set => SetProperty(ref _studioMovesPanelWidth, Math.Clamp(value, 300, 620));
    }

    public double StudioGamesPanelWidth
    {
        get => _studioGamesPanelWidth;
        private set => SetProperty(ref _studioGamesPanelWidth, Math.Clamp(value, 230, 520));
    }

    public GridLength StudioMovesColumnWidth => new(StudioMovesPanelWidth);
    public GridLength StudioGamesColumnWidth => new(StudioGamesPanelWidth);

    public StudioAudioItem? SelectedAudio
    {
        get => _selectedAudio;
        set
        {
            if (SetProperty(ref _selectedAudio, value))
            {
                _openedAudioId = null;
                _audioPlayer.Stop();
                OnPropertyChanged(nameof(CanDeleteSelectedAudio));
                PlayAudioCommand.RaiseCanExecuteChanged();
                DeleteAudioCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool CanDeleteSelectedAudio
    {
        get
        {
            var metadata = SelectedAudio?.Metadata;
            if (metadata is null || IsRecording)
            {
                return false;
            }

            if (string.Equals(metadata.Scope, "user", StringComparison.Ordinal))
            {
                // The server only returns the signed-in user's private audio, so every
                // user-scoped row present in this local workspace is owned by that user.
                return true;
            }

            // Public/course audio is server-admin-only. A not-yet-uploaded local
            // recording can still be discarded offline by the authoring installation.
            return string.Equals(metadata.Scope, "course", StringComparison.Ordinal) &&
                   (IsAdmin || (metadata.Dirty && string.IsNullOrWhiteSpace(metadata.ServerId)));
        }
    }

    public bool IsRecording
    {
        get => _isRecording;
        private set
        {
            if (SetProperty(ref _isRecording, value))
            {
                OnPropertyChanged(nameof(CanDeleteSelectedAudio));
                RaiseAudioCommandStates();
            }
        }
    }

    public bool IsAudioPlaying
    {
        get => _isAudioPlaying;
        private set
        {
            if (SetProperty(ref _isAudioPlaying, value))
            {
                OnPropertyChanged(nameof(AudioPlayLabel));
            }
        }
    }

    public string AudioPlayLabel => IsAudioPlaying ? "توقف" : "پخش";

    public long AudioPositionMilliseconds
    {
        get => _audioPositionMilliseconds;
        set
        {
            var safe = Math.Clamp(value, 0, Math.Max(1, AudioDurationMaximum));
            if (SetProperty(ref _audioPositionMilliseconds, safe) && !_updatingAudioPosition)
            {
                _audioPlayer.Seek(safe);
            }

            OnPropertyChanged(nameof(AudioTimeLabel));
        }
    }

    public long AudioDurationMilliseconds
    {
        get => _audioDurationMilliseconds;
        private set
        {
            if (SetProperty(ref _audioDurationMilliseconds, Math.Max(0, value)))
            {
                OnPropertyChanged(nameof(AudioDurationMaximum));
                OnPropertyChanged(nameof(AudioTimeLabel));
            }
        }
    }

    public long AudioDurationMaximum => Math.Max(1, AudioDurationMilliseconds);

    public string AudioTimeLabel =>
        $"{FormatDuration(AudioPositionMilliseconds)} / {FormatDuration(AudioDurationMilliseconds)}";

    public string RecordingStatus
    {
        get => _recordingStatus;
        private set => SetProperty(ref _recordingStatus, value);
    }

    public string TranslationCacheSearch
    {
        get => _translationCacheSearch;
        set => SetProperty(ref _translationCacheSearch, value);
    }

    public TranslationCacheEntry? SelectedTranslationCacheEntry
    {
        get => _selectedTranslationCacheEntry;
        set
        {
            if (SetProperty(ref _selectedTranslationCacheEntry, value))
            {
                TranslationCacheEditor = value?.TranslatedText ?? string.Empty;
                ApplyTranslationCacheEditCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string TranslationCacheEditor
    {
        get => _translationCacheEditor;
        set
        {
            if (SetProperty(ref _translationCacheEditor, value))
            {
                ApplyTranslationCacheEditCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string CurrentFileName =>
        Workspace.SourceNames.Count == 0
            ? "بدون فایل"
            : Workspace.SourceNames.Count == 1
                ? Workspace.SourceNames[0]
                : $"{Workspace.SourceNames[0]} +{Workspace.SourceNames.Count - 1}";

    public string CurrentMoveLabel
    {
        get
        {
            var node = Session.CurrentNode;
            if (node is null || node.IsRoot)
            {
                return "شروع بازی";
            }

            var number = node.FullmoveNumber ?? Math.Max(1, (node.Ply + 1) / 2);
            var isWhite = node.IsWhiteMove ?? node.Ply % 2 == 1;
            return $"{number}{(isWhite ? "." : "...")} " +
                   ViewerNotation.FormatSan(node.RawSan, isWhite, _notationMode);
        }
    }

    public string TopPlayerName => Orientation == BoardOrientation.White
        ? Session.ActiveGame?.Black ?? "سیاه"
        : Session.ActiveGame?.White ?? "سفید";

    public string BottomPlayerName => Orientation == BoardOrientation.White
        ? Session.ActiveGame?.White ?? "سفید"
        : Session.ActiveGame?.Black ?? "سیاه";

    public bool IsBranchChooserOpen => Session.IsBranchChooserOpen;
    public int GameCount => Games.Count;
    public int NodeCount => Games.Sum(static game => game.Game.NodeCount);

    public async Task InitializeAsync()
    {
        try
        {
            await _database.InitializeAsync(_lifetime.Token).ConfigureAwait(true);
            _settings = await _settingsRepository.LoadAsync(_lifetime.Token).ConfigureAwait(true);
            _localInstallationId = string.IsNullOrWhiteSpace(_settings.LocalInstallationId)
                ? $"device:{Guid.NewGuid():N}"
                : _settings.LocalInstallationId;
            if (!string.Equals(_settings.LocalInstallationId, _localInstallationId, StringComparison.Ordinal))
            {
                _settings = await _settingsRepository.UpdateAsync(
                    current => current with { LocalInstallationId = _localInstallationId },
                    _lifetime.Token).ConfigureAwait(true);
            }

            _selectedSkin = _settings.BoardSkin;
            _showCoordinates = _settings.ShowCoordinates;
            _moveSoundEnabled = _settings.MoveSoundEnabled;
            _headerCollapsed = _settings.HeaderCollapsed;
            _commentFontSize = AllowedCommentFontSizes.Contains(_settings.CommentFontSize)
                ? _settings.CommentFontSize
                : 14;
            _commentFontFamily = CommentFontService.Resolve(
                _settings.CommentFontFamilyName,
                _settings.CustomCommentFontPath);
            _notationMode = Enum.TryParse<ViewerNotationMode>(
                _settings.ViewerNotationMode,
                ignoreCase: true,
                out var notationMode)
                ? notationMode
                : ViewerNotationMode.Letters;
            _translationConcurrency = Math.Clamp(_settings.TranslationConcurrency, 1, 12);
            _serverBaseUrl = _settings.ServerBaseUrl;
            _studioMovesPanelWidth = Math.Clamp(_settings.StudioMovesPanelWidth, 300, 620);
            _studioGamesPanelWidth = Math.Clamp(_settings.StudioGamesPanelWidth, 230, 520);
            foreach (var property in new[]
                     {
                         nameof(SelectedSkin), nameof(ShowCoordinates), nameof(MoveSoundEnabled),
                         nameof(HeaderCollapsed), nameof(CommentFontSize), nameof(CommentFontFamily),
                         nameof(CommentFontLabel), nameof(SelectedBuiltInCommentFontFamily),
                         nameof(SelectedNotationMode),
                         nameof(TranslationConcurrency), nameof(ServerBaseUrl),
                         nameof(StudioMovesPanelWidth), nameof(StudioGamesPanelWidth),
                         nameof(StudioMovesColumnWidth), nameof(StudioGamesColumnWidth),
                     })
            {
                OnPropertyChanged(property);
            }

            var soundWarning = string.Empty;
            try
            {
                await _moveSound.InitializeAsync(_lifetime.Token).ConfigureAwait(true);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _moveSoundEnabled = false;
                OnPropertyChanged(nameof(MoveSoundEnabled));
                soundWarning = $"؛ صدای حرکت غیرفعال شد: {exception.Message}";
            }

            await ReloadDraftsAsync(_lifetime.Token).ConfigureAwait(true);
            SyncQueueCount = await _syncQueue.CountAsync(_lifetime.Token).ConfigureAwait(true);
            Status = "PGN Studio آماده است؛ فایل باز کنید یا یک Draft محلی را ادامه دهید." + soundWarning;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Status = $"راه‌اندازی Studio کامل نشد: {exception.Message}";
        }
    }

    public void ReportStartupFailure(string message) =>
        Status = $"راه‌اندازی Studio کامل نشد: {message}";

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
        Status = append ? "در حال افزودن PGN در Worker…" : "در حال خواندن PGN در Worker…";
        try
        {
            var batch = await _documentLoader.LoadAsync(paths, run.Token).ConfigureAwait(true);
            if (batch.Sources.Count == 0)
            {
                Status = batch.Diagnostics.FirstOrDefault() ?? "PGN معتبر پیدا نشد.";
                return;
            }

            if (append && Session.HasGames)
            {
                Workspace.Append(batch.Sources);
            }
            else
            {
                Workspace.Replace(batch.Sources);
                DraftId = $"draft_{Guid.NewGuid():N}";
                ServerDraftId = null;
                ServerCourseId = null;
                DraftTitle = Path.GetFileNameWithoutExtension(paths[0]);
                PublishSlug = Slugify(DraftTitle);
                CategorySlug = "training";
                CreditPriceMinor = 0;
                FeaturedImagePath = string.Empty;
                FeaturedImageName = string.Empty;
                await SaveLocalDraftAsync("import", run.Token).ConfigureAwait(true);
            }

            await RefreshAudioForCurrentNodeAsync(includeServer: false, run.Token).ConfigureAwait(true);

            Metrics.PgnParseMilliseconds = batch.ParseMilliseconds;
            Metrics.PgnSemanticMilliseconds = batch.SemanticMilliseconds;
            RefreshMetrics();
            Status = $"{batch.GameCount} بازی و {batch.NodeCount} گره در Studio بارگذاری شد.";
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

    public async Task SavePgnAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var pgn = Workspace.ExportPgn();
        await File.WriteAllTextAsync(path, pgn, new System.Text.UTF8Encoding(false), cancellationToken)
            .ConfigureAwait(true);
        Status = $"PGN با {GameCount} بازی ذخیره شد.";
    }

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

    public async Task SelectFeaturedImageAsync(
        string filePath,
        CancellationToken cancellationToken = default)
    {
        var image = await FeaturedImageService.InstallAsync(filePath, cancellationToken).ConfigureAwait(true);
        FeaturedImagePath = image.FilePath;
        FeaturedImageName = image.FileName;
        MarkMetadataChanged();
        Status = $"تصویر شاخص «{image.FileName}» برای Draft انتخاب شد.";
    }

    public void ClearFeaturedImage()
    {
        FeaturedImagePath = string.Empty;
        FeaturedImageName = string.Empty;
        MarkMetadataChanged();
        Status = "تصویر شاخص محلی از Draft برداشته شد.";
    }

    public async Task HandleCandidateMoveAsync(
        Square from,
        Square to,
        char? promotion,
        CancellationToken cancellationToken = default)
    {
        var startingNode = Session.CurrentNode;
        var game = Session.ActiveGame?.Game;
        if (startingNode is null || game is null)
        {
            return;
        }

        _candidateRun?.Cancel();
        _candidateRun?.Dispose();
        var run = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, cancellationToken);
        _candidateRun = run;
        var nodeId = startingNode.StableId;
        var fen = BoardFen;
        try
        {
            var moves = await Task.Run(
                async () => await _chessRules.GetLegalMovesAsync(fen, run.Token).ConfigureAwait(false),
                run.Token).ConfigureAwait(true);
            if (!string.Equals(Session.CurrentNode?.StableId, nodeId, StringComparison.Ordinal))
            {
                return;
            }

            var legal = moves.FirstOrDefault(move =>
                move.From == from &&
                move.To == to &&
                (promotion is null || move.Promotion == char.ToLowerInvariant(promotion.Value)));
            if (legal is null)
            {
                Status = "این حرکت قانونی نیست.";
                return;
            }

            var resultingFen = await Task.Run(
                async () => await _chessRules.ApplyMoveAsync(fen, legal.Uci, run.Token).ConfigureAwait(false),
                run.Token).ConfigureAwait(true);
            var result = Workspace.AddMove(legal, resultingFen);
            if (MoveSoundEnabled)
            {
                _moveSound.Play(MoveSoundClassifier.FromSan(legal.San));
            }
            Status = result.Created
                ? $"شاخه {legal.San} به PGN افزوده شد."
                : $"شاخه موجود {legal.San} انتخاب شد.";
        }
        catch (OperationCanceledException) when (run.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Status = $"افزودن حرکت ناموفق بود: {exception.Message}";
        }
    }

    public void SelectNode(string nodeId)
    {
        var previous = Session.CurrentNode;
        if (Session.SelectNode(nodeId) && previous is not null && Session.CurrentNode is not null)
        {
            PlayNavigationSound(previous, Session.CurrentNode);
        }
    }

    public bool DeleteCurrentBranch()
    {
        var deleted = Workspace.DeleteCurrentBranch();
        Status = deleted ? "شاخه کامل حذف شد." : "ریشه بازی قابل حذف نیست.";
        return deleted;
    }

    public bool RemoveGame(ViewerGameItem game)
    {
        var removed = Workspace.RemoveGame(game);
        if (removed)
        {
            Status = $"بازی حذف شد؛ {GameCount} بازی باقی ماند.";
        }

        return removed;
    }

    public int RemoveMarkedGames()
    {
        var removed = Workspace.RemoveMarkedGames();
        if (removed > 0)
        {
            Status = $"{removed} بازی انتخاب‌شده حذف شد؛ {GameCount} بازی باقی ماند.";
        }

        return removed;
    }

    public async Task LoginAsync(
        string identifier,
        string password,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(identifier) || string.IsNullOrEmpty(password))
        {
            Status = "نام کاربری و رمز عبور را وارد کنید.";
            return;
        }

        try
        {
            EnsureServerClient();
            var login = await _server!.LoginAsync(
                new LoginRequest(identifier.Trim(), password),
                cancellationToken).ConfigureAwait(true);
            IsConnected = true;
            IsAuthenticated = login.Ok;
            ApplyUser(login.User);
            AuthStatus = IsAdmin ? "متصل · مدیر" : "متصل · کاربر";
            await LoadCategoriesAsync(cancellationToken).ConfigureAwait(true);
            if (IsAuthenticated)
            {
                await FlushSyncQueueAsync(cancellationToken).ConfigureAwait(true);
                await RefreshAudioForCurrentNodeAsync(includeServer: true, cancellationToken).ConfigureAwait(true);
            }
            Status = "ورود به Server انجام شد.";
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            IsAuthenticated = false;
            IsAdmin = false;
            _currentServerUserId = null;
            Status = $"ورود ناموفق بود: {exception.Message}";
        }
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
        Metrics.RenderMilliseconds = milliseconds;
        var now = Stopwatch.GetTimestamp();
        if (_lastRenderMetricTimestamp == 0 ||
            Stopwatch.GetElapsedTime(_lastRenderMetricTimestamp, now) >= TimeSpan.FromMilliseconds(250))
        {
            _lastRenderMetricTimestamp = now;
            Metrics.RefreshMemory();
        }
    }

    public void UpdatePanelWidths(double movesWidth, double gamesWidth)
    {
        StudioMovesPanelWidth = movesWidth;
        StudioGamesPanelWidth = gamesWidth;
        OnPropertyChanged(nameof(StudioMovesColumnWidth));
        OnPropertyChanged(nameof(StudioGamesColumnWidth));
        ScheduleSettingsSave();
    }

    public void ReportPositionError(string message) =>
        Status = $"FEN نامعتبر: {message}";

    private async Task ApplyCommentsCommandAsync(CancellationToken cancellationToken)
    {
        try
        {
            await ApplyCommentsAsync(cancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Status = $"ویرایش توضیحات کامل نشد: {exception.Message}";
        }
    }

    private async Task ApplyCommentsAsync(CancellationToken cancellationToken)
    {
        var game = Session.ActiveGame?.Game;
        var node = Session.CurrentNode;
        if (game is null || node is null)
        {
            return;
        }

        _ = Workspace.TryGetTranslationLink(game.Id, node.StableId, "startingComment", out var startingLink);
        _ = Workspace.TryGetTranslationLink(game.Id, node.StableId, "comment", out var commentLink);
        var oldStartingText = PgnTreeEditor.StartingCommentText(node);
        var oldCommentText = PgnTreeEditor.CommentText(node);
        var startingText = StartingCommentEditor;
        var commentText = CommentEditor;
        Workspace.EditCurrentComments(startingText, commentText);
        if (startingLink is not null)
        {
            Workspace.SetTranslationLink(startingLink);
        }

        if (commentLink is not null)
        {
            Workspace.SetTranslationLink(commentLink);
        }

        var updates = new[]
        {
            (Link: startingLink, Previous: oldStartingText, Translation: startingText),
            (Link: commentLink, Previous: oldCommentText, Translation: commentText),
        }.Where(static item => item.Link is not null &&
                               !string.IsNullOrWhiteSpace(item.Translation) &&
                               !string.Equals(item.Previous, item.Translation, StringComparison.Ordinal)).ToArray();
        if (updates.Length == 0)
        {
            Status = "توضیحات گره ویرایش شد؛ Autosave در صف قرار گرفت.";
            return;
        }

        var queued = 0;
        foreach (var update in updates)
        {
            var link = update.Link!;
            try
            {
                var server = _server;
                if (server is null || !IsConnected || !IsAuthenticated || !IsAdmin)
                {
                    throw new HttpRequestException("Server is offline.");
                }

                _ = await server.UpdateTranslationMemoryAsync(
                    link.SourceHash,
                    link.SourceText,
                    update.Translation,
                    cancellationToken).ConfigureAwait(true);
                await _translationCache.UpsertManyAsync(
                    [new TranslationCacheEntry(
                        link.SourceHash,
                        "en",
                        "fa",
                        link.SourceText,
                        update.Translation,
                        "approved",
                        null,
                        game.Id,
                        node.StableId,
                        null,
                        DateTimeOffset.UtcNow)],
                    cancellationToken).ConfigureAwait(true);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                try
                {
                    var pending = new PendingTranslationMemoryUpdate(
                        link.SourceHash,
                        link.SourceText,
                        update.Translation);
                    await _syncQueue.EnqueueAsync(
                        $"translation-memory:{link.SourceHash}",
                        "translation-memory-update",
                        "translation-memory",
                        link.SourceHash,
                        JsonSerializer.Serialize(pending, JsonOptions),
                        cancellationToken: cancellationToken).ConfigureAwait(true);
                    queued++;
                }
                catch (Exception queueException) when (queueException is not OperationCanceledException)
                {
                    Status = $"ویرایش محلی انجام شد ولی صف Sync ذخیره نشد: {queueException.Message}";
                }
            }
        }

        SyncQueueCount = await _syncQueue.CountAsync(cancellationToken).ConfigureAwait(true);

        Status = queued == 0
            ? "توضیحات و Translation Memory سرور به‌روزرسانی شدند."
            : $"توضیحات ذخیره شد؛ {queued} ویرایش TM برای Sync بعدی باقی ماند.";
    }

    private void PromoteBranch()
    {
        if (Workspace.PromoteCurrentBranch())
        {
            Status = "شاخه انتخابی به Mainline منتقل شد.";
        }
    }

    private bool CanMutateCurrentBranch() => Session.CurrentNode?.Parent is not null;

    private void FlipBoard() => Orientation = Orientation == BoardOrientation.White
        ? BoardOrientation.Black
        : BoardOrientation.White;

    private void PreviousMove()
    {
        var previous = Session.CurrentNode;
        if (Session.PreviousMove() && previous is not null && Session.CurrentNode is not null)
        {
            PlayNavigationSound(previous, Session.CurrentNode);
            Status = "حرکت قبل انتخاب شد.";
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
            Status = "شاخه ادامه را انتخاب کنید.";
        }
    }

    private void NavigateGame(int offset)
    {
        if (Session.SelectGameByOffset(offset))
        {
            Status = $"بازی {Session.ActiveGame?.FullTitle} انتخاب شد.";
        }
    }

    private void ChooseBranch()
    {
        var previous = Session.CurrentNode;
        if (SelectedBranch is not null && Session.SelectBranch(SelectedBranch.Index))
        {
            if (previous is not null && Session.CurrentNode is not null)
            {
                PlayNavigationSound(previous, Session.CurrentNode);
            }

            Status = $"شاخه {SelectedBranch.Label} انتخاب شد.";
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
            _moveSound.Play(MoveSoundClassifier.FromSan(soundedNode.RawSan));
        }
    }

    private async Task RefreshLegalMovesAsync()
    {
        _legalRun?.Cancel();
        _legalRun?.Dispose();
        var node = Session.CurrentNode;
        if (node is null)
        {
            LegalMoves = Array.Empty<LegalMove>();
            return;
        }

        var run = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _legalRun = run;
        var nodeId = node.StableId;
        var fen = BoardFen;
        try
        {
            var moves = await Task.Run(
                async () => await _chessRules.GetLegalMovesAsync(fen, run.Token).ConfigureAwait(false),
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
            Status = $"محاسبه حرکات قانونی ناموفق بود: {exception.Message}";
        }
    }

    private async Task SaveLocalDraftAsync(string reason, CancellationToken cancellationToken)
    {
        if (!Session.HasGames)
        {
            return;
        }

        if (!string.Equals(reason, "autosave", StringComparison.Ordinal))
        {
            _autosaveRun?.Cancel();
            _autosaveRun?.Dispose();
            _autosaveRun = null;
        }

        var draftId = DraftId ??= $"draft_{Guid.NewGuid():N}";
        var package = Workspace.CreateDraftPackage(
            draftId,
            sourceId: Workspace.SourceNames.FirstOrDefault(),
            DraftTitle,
            ServerDraftId,
            CategorySlug,
            PublishSlug,
            CreditPriceMinor,
            FeaturedImagePath,
            FeaturedImageName,
            serverCourseId: ServerCourseId);
        var json = JsonSerializer.Serialize(package, JsonOptions);
        var stopwatch = Stopwatch.StartNew();
        await _draftRepository.SaveAsync(
            draftId,
            package.SourceId,
            DraftTitle,
            json,
            reason,
            dirty: Workspace.IsDirty || _metadataDirty,
            serverRevision: (ServerDraftId ?? ServerCourseId)?.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            cancellationToken).ConfigureAwait(true);
        stopwatch.Stop();
        Metrics.DatabaseMilliseconds = stopwatch.Elapsed.TotalMilliseconds;
        Workspace.MarkSaved();
        _metadataDirty = false;
        var pendingDraftSyncId = $"studio-draft:{draftId}";
        if (!string.Equals(reason, "server-draft", StringComparison.Ordinal) &&
            !string.Equals(reason, "published", StringComparison.Ordinal) &&
            (ServerDraftId is not null ||
             await _syncQueue.ContainsAsync(pendingDraftSyncId, cancellationToken).ConfigureAwait(true)))
        {
            var currentRequest = new StudioDraftRequest(
                DraftTitle,
                CategorySlug,
                Workspace.BuildServerPayload(),
                CurrentFileName,
                ServerDraftId,
                CreditPriceMinor);
            await _syncQueue.EnqueueAsync(
                pendingDraftSyncId,
                "studio-draft-save",
                "studio-draft",
                draftId,
                JsonSerializer.Serialize(
                    new PendingStudioDraftSave(draftId, currentRequest, FeaturedImagePath),
                    JsonOptions),
                expectedRevision: ServerDraftId?.ToString(System.Globalization.CultureInfo.InvariantCulture),
                cancellationToken).ConfigureAwait(true);
        }

        if (!string.Equals(reason, "autosave", StringComparison.Ordinal))
        {
            _autosaveRun?.Cancel();
            _autosaveRun?.Dispose();
            _autosaveRun = null;
        }

        await ReloadDraftsAsync(cancellationToken).ConfigureAwait(true);
        Status = reason == "autosave" ? "Autosave محلی انجام شد." : "Draft محلی ذخیره شد.";
    }

    private async Task SaveLocalDraftCommandAsync(CancellationToken cancellationToken)
    {
        try
        {
            await SaveLocalDraftAsync("explicit-save", cancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Status = $"ذخیره Draft محلی ناموفق بود: {exception.Message}";
        }
    }

    private async Task ResumeSelectedDraftAsync(CancellationToken cancellationToken)
    {
        var selected = SelectedLocalDraft;
        if (selected is null)
        {
            return;
        }

        try
        {
            IsBusy = true;
            var record = await _draftRepository.GetAsync(selected.Id, cancellationToken).ConfigureAwait(true)
                ?? throw new InvalidDataException("Draft محلی پیدا نشد.");
            var package = JsonSerializer.Deserialize<StudioDraftPackage>(record.PayloadJson, JsonOptions)
                ?? throw new InvalidDataException("ساختار Draft محلی معتبر نیست.");
            await Workspace.RestoreAsync(package, _documentLoader, cancellationToken).ConfigureAwait(true);
            ApplyDraftMetadata(package);
            await RefreshAudioForCurrentNodeAsync(IsConnected, cancellationToken).ConfigureAwait(true);
            Status = $"Draft «{DraftTitle}» از Revision {record.CurrentRevision} باز شد.";
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Status = $"بازکردن Draft ناموفق بود: {exception.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task TranslateAsync(CancellationToken commandToken)
    {
        var courseIdentity = (ServerCourseId ?? ServerDraftId)?.ToString(
            System.Globalization.CultureInfo.InvariantCulture) ?? DraftId;
        var work = Workspace.CollectTranslationWork(courseIdentity);
        TranslationTotal = work.Count;
        TranslationCompleted = 0;
        TranslationFailed = 0;
        TranslationPercentage = work.Count == 0 ? 100 : 0;
        TranslationMessage = work.Count == 0 ? "توضیح انگلیسی برای ترجمه پیدا نشد." : "آماده‌سازی ترجمه…";
        if (work.Count == 0)
        {
            return;
        }

        TranslationInProgress = true;
        var lastPresentationRefresh = Stopwatch.GetTimestamp();
        try
        {
            EnsureServerClient();
            var queue = new TranslationQueue(_server!, _translationCache);
            var progress = new Progress<TranslationQueueProgress>(update =>
            {
                TranslationTotal = update.Total;
                TranslationCompleted = update.Completed;
                TranslationFailed = update.Failed;
                TranslationPercentage = update.Percentage;
                TranslationMessage = update.Message;
                if (update.Applied is not null)
                {
                    _ = Workspace.ApplyTranslation(update.Applied, refreshPresentation: false);
                }

                var now = Stopwatch.GetTimestamp();
                if (update.Applied is not null &&
                    Stopwatch.GetElapsedTime(lastPresentationRefresh, now) >= TimeSpan.FromMilliseconds(120))
                {
                    Workspace.RefreshActivePresentation();
                    lastPresentationRefresh = now;
                }
            });
            var result = await queue.RunAsync(
                work,
                new TranslationQueueOptions(TranslationConcurrency),
                progress,
                commandToken).ConfigureAwait(true);
            Workspace.RefreshActivePresentation();
            var queued = await _translationBacklog.EnqueueFailuresAsync(
                result.Failures,
                commandToken).ConfigureAwait(true);
            SyncQueueCount = await _syncQueue.CountAsync(commandToken).ConfigureAwait(true);
            TranslationMessage = result.IsComplete
                ? $"ترجمه کامل شد؛ Cache {result.CacheHits}، TM {result.ServerMemoryHits}، Server {result.ServerTranslated}."
                : $"نتیجه جزئی ذخیره شد؛ {result.Failures.Sum(static failure => failure.Items.Count)} مورد باقی ماند" +
                  (queued > 0 ? $" و {queued} درخواست برای اتصال بعدی صف شد." : ".");
            Status = TranslationMessage;
        }
        catch (OperationCanceledException) when (commandToken.IsCancellationRequested)
        {
            Workspace.RefreshActivePresentation();
            TranslationMessage = "ترجمه لغو شد؛ نتایج دریافت‌شده حفظ شدند.";
            Status = TranslationMessage;
        }
        catch (Exception exception)
        {
            Workspace.RefreshActivePresentation();
            TranslationMessage = $"ترجمه متوقف شد: {exception.Message}";
            Status = TranslationMessage;
        }
        finally
        {
            TranslationInProgress = false;
            if (Workspace.IsDirty)
            {
                ScheduleAutosave();
            }
        }
    }

    private async Task SearchTranslationCacheAsync(CancellationToken cancellationToken)
    {
        try
        {
            var started = Stopwatch.GetTimestamp();
            var entries = await _translationCache.SearchAsync(
                TranslationCacheSearch.Trim(),
                offset: 0,
                limit: 250,
                cancellationToken).ConfigureAwait(true);
            TranslationCacheRows.Clear();
            foreach (var entry in entries)
            {
                TranslationCacheRows.Add(entry);
            }

            Metrics.DatabaseMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            SelectedTranslationCacheEntry = TranslationCacheRows.FirstOrDefault();
            Status = entries.Count == 0
                ? "موردی در Translation Cache پیدا نشد."
                : $"{entries.Count} رکورد Translation Cache بارگذاری شد.";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Status = $"خواندن Translation Cache ناموفق بود: {exception.Message}";
        }
    }

    private async Task ApplyTranslationCacheEditAsync(CancellationToken cancellationToken)
    {
        var selected = SelectedTranslationCacheEntry;
        var editedText = TranslationCacheEditor.Trim();
        if (selected is null || string.IsNullOrWhiteSpace(editedText))
        {
            return;
        }

        var updated = selected with
        {
            TranslatedText = editedText,
            Status = "local-approved-pending-sync",
            UpdatedUtc = DateTimeOffset.UtcNow,
        };
        var propagated = 0;

        try
        {
            await _translationCache.UpsertManyAsync([updated], cancellationToken).ConfigureAwait(true);
            propagated = Workspace.ApplyTranslationMemoryUpdate(
                updated.PhraseIdentity,
                updated.SourceText,
                updated.TranslatedText);
            var server = _server;
            if (server is not null && IsConnected && IsAuthenticated && IsAdmin)
            {
                _ = await server.UpdateTranslationMemoryAsync(
                    updated.PhraseIdentity,
                    updated.SourceText,
                    updated.TranslatedText,
                    cancellationToken).ConfigureAwait(true);
                updated = updated with
                {
                    Status = "approved",
                    UpdatedUtc = DateTimeOffset.UtcNow,
                };
                await _translationCache.UpsertManyAsync([updated], cancellationToken).ConfigureAwait(true);
                await _syncQueue.CompleteAsync(
                    $"translation-memory:{updated.PhraseIdentity}",
                    cancellationToken).ConfigureAwait(true);
                Status = $"ترجمه در Cache و TM سرور تأیید شد؛ {propagated} محل محلی به‌روزرسانی شد.";
            }
            else
            {
                await QueueTranslationMemoryUpdateAsync(updated, cancellationToken).ConfigureAwait(true);
                Status = $"ترجمه در Cache ذخیره شد؛ {propagated} محل به‌روزرسانی و Sync صف شد.";
            }

            ReplaceTranslationCacheRow(selected, updated);
            SyncQueueCount = await _syncQueue.CountAsync(cancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            try
            {
                await QueueTranslationMemoryUpdateAsync(updated, cancellationToken).ConfigureAwait(true);
                ReplaceTranslationCacheRow(selected, updated);
                SyncQueueCount = await _syncQueue.CountAsync(cancellationToken).ConfigureAwait(true);
                Status = $"ویرایش محلی در {propagated} محل محفوظ است و بعداً Sync می‌شود: {exception.Message}";
            }
            catch (Exception queueException) when (queueException is not OperationCanceledException)
            {
                Status = $"ویرایش Cache ذخیره شد ولی ثبت صف Sync ناموفق بود: {queueException.Message}";
            }
        }
    }

    private Task QueueTranslationMemoryUpdateAsync(
        TranslationCacheEntry entry,
        CancellationToken cancellationToken)
    {
        var pending = new PendingTranslationMemoryUpdate(
            entry.PhraseIdentity,
            entry.SourceText,
            entry.TranslatedText);
        return _syncQueue.EnqueueAsync(
            $"translation-memory:{entry.PhraseIdentity}",
            "translation-memory-update",
            "translation-memory",
            entry.PhraseIdentity,
            JsonSerializer.Serialize(pending, JsonOptions),
            cancellationToken: cancellationToken);
    }

    private void ReplaceTranslationCacheRow(
        TranslationCacheEntry previous,
        TranslationCacheEntry updated)
    {
        var index = TranslationCacheRows.IndexOf(previous);
        if (index >= 0)
        {
            TranslationCacheRows[index] = updated;
        }

        SelectedTranslationCacheEntry = updated;
    }

    private async Task ConnectAsync(CancellationToken cancellationToken)
    {
        try
        {
            EnsureServerClient();
            AuthStatus = "در حال اتصال…";
            var session = await _server!.GetSessionAsync(cancellationToken).ConfigureAwait(true);
            IsConnected = true;
            IsAuthenticated = session.Authenticated;
            if (session.User is { } user)
            {
                ApplyUser(user);
            }
            else
            {
                IsAdmin = false;
                _currentServerUserId = null;
            }

            AuthStatus = !session.Authenticated
                ? "متصل · ورود لازم است"
                : IsAdmin ? "متصل · مدیر" : "متصل · کاربر";
            await LoadCategoriesAsync(cancellationToken).ConfigureAwait(true);
            await FlushSyncQueueAsync(cancellationToken).ConfigureAwait(true);
            if (IsAuthenticated)
            {
                await RefreshAudioForCurrentNodeAsync(includeServer: true, cancellationToken).ConfigureAwait(true);
            }
            Status = "اتصال Server برقرار شد.";
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            IsConnected = false;
            IsAuthenticated = false;
            IsAdmin = false;
            _currentServerUserId = null;
            AuthStatus = "آفلاین";
            Status = $"Server در دسترس نیست: {exception.Message}";
        }
    }

    private async Task SaveServerDraftAsync(CancellationToken cancellationToken)
    {
        var server = _server;
        if (server is null || !CanUseAdminServer())
        {
            return;
        }

        StudioDraftRequest? request = null;
        try
        {
            var featuredImageData = await FeaturedImageService.ToDataUriAsync(
                FeaturedImagePath,
                cancellationToken).ConfigureAwait(true);
            request = new StudioDraftRequest(
                DraftTitle,
                CategorySlug,
                Workspace.BuildServerPayload(),
                CurrentFileName,
                ServerDraftId,
                CreditPriceMinor,
                featuredImageData);
            var response = await server.SaveStudioDraftAsync(
                request,
                cancellationToken).ConfigureAwait(true);
            if (!TryReadServerDraftId(response, out var serverId))
            {
                throw new InvalidDataException("Server draft response has no valid ID.");
            }

            ServerDraftId = serverId;
            ServerCourseId = null;

            Workspace.MarkSaved();
            await SaveLocalDraftAsync("server-draft", cancellationToken).ConfigureAwait(true);
            if (DraftId is { } savedDraftId)
            {
                await _syncQueue.CompleteAsync($"studio-draft:{savedDraftId}", cancellationToken).ConfigureAwait(true);
            }

            await FlushSyncQueueAsync(cancellationToken).ConfigureAwait(true);
            await RefreshAudioForCurrentNodeAsync(includeServer: true, cancellationToken).ConfigureAwait(true);
            Status = $"Draft روی Server ذخیره شد (ID: {ServerDraftId}).";
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Status = $"ذخیره Server Draft ناموفق بود؛ نسخه محلی محفوظ است: {exception.Message}";
            try
            {
                if (request is not null)
                {
                    var localDraftId = DraftId ??= $"draft_{Guid.NewGuid():N}";
                    await _syncQueue.EnqueueAsync(
                        $"studio-draft:{localDraftId}",
                        "studio-draft-save",
                        "studio-draft",
                        localDraftId,
                        JsonSerializer.Serialize(
                            new PendingStudioDraftSave(
                                localDraftId,
                                request with { FeaturedImageData = string.Empty },
                                FeaturedImagePath),
                            JsonOptions),
                        expectedRevision: ServerDraftId?.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        cancellationToken).ConfigureAwait(true);
                    SyncQueueCount = await _syncQueue.CountAsync(cancellationToken).ConfigureAwait(true);
                    Status += "؛ به صف Sync اضافه شد.";
                }
            }
            catch (Exception queueException) when (queueException is not OperationCanceledException)
            {
                Status += $"؛ ثبت صف Sync ناموفق بود: {queueException.Message}";
            }

            try
            {
                await SaveLocalDraftAsync("server-save-failed", cancellationToken).ConfigureAwait(true);
            }
            catch (Exception localException) when (localException is not OperationCanceledException)
            {
                Status += $"؛ ذخیره محلی هم ناموفق بود: {localException.Message}";
            }
        }
    }

    private async Task PublishAsync(CancellationToken cancellationToken)
    {
        var server = _server;
        if (server is null || !CanUseAdminServer())
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(DraftTitle) || string.IsNullOrWhiteSpace(PublishSlug))
        {
            Status = "عنوان و Slug برای انتشار الزامی است.";
            return;
        }

        try
        {
            var featuredImageData = await FeaturedImageService.ToDataUriAsync(
                FeaturedImagePath,
                cancellationToken).ConfigureAwait(true);
            var response = await server.PublishCourseAsync(
                new StudioPublishRequest(
                    DraftTitle,
                    PublishSlug,
                    CategorySlug,
                    Workspace.BuildServerPayload(),
                    CurrentFileName,
                    ServerDraftId,
                    CreditPriceMinor,
                    featuredImageData),
                cancellationToken).ConfigureAwait(true);
            var hasPublishedCourseId = TryReadPublishedCourseId(response, out var publishedCourseId);
            if (hasPublishedCourseId)
            {
                // The server consumes the draft row and turns it into a published course.
                // Keep those identities separate: Draft APIs must not target a published row,
                // while move-audio continues to use the published course ID.
                ServerCourseId = publishedCourseId;
                ServerDraftId = null;
            }

            Workspace.MarkSaved();
            await SaveLocalDraftAsync("published", cancellationToken).ConfigureAwait(true);
            if (hasPublishedCourseId)
            {
                if (DraftId is { } publishedDraftId)
                {
                    await _syncQueue.CompleteAsync(
                        $"studio-draft:{publishedDraftId}",
                        cancellationToken).ConfigureAwait(true);
                }

                await FlushSyncQueueAsync(cancellationToken).ConfigureAwait(true);
                await RefreshAudioForCurrentNodeAsync(includeServer: true, cancellationToken).ConfigureAwait(true);
            }

            var courseTitle = response.TryGetProperty("course", out var course) &&
                              course.TryGetProperty("title", out var title)
                ? title.GetString() ?? DraftTitle
                : DraftTitle;
            Status = hasPublishedCourseId
                ? $"دوره «{courseTitle}» منتشر شد (ID: {publishedCourseId})."
                : $"دوره «{courseTitle}» منتشر شد؛ پاسخ Server شناسهٔ Course نداشت و Audioهای صف‌شده فعلاً محلی می‌مانند.";
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Status = $"انتشار ناموفق بود؛ Draft محلی محفوظ است: {exception.Message}";
        }
    }

    private async Task LoadServerDraftAsync(CancellationToken cancellationToken)
    {
        var server = _server;
        if (server is null || !CanUseAdminServer() ||
            !long.TryParse(ServerDraftIdText, out var draftId) || draftId <= 0)
        {
            Status = "ID عددی Draft سرور را وارد کنید.";
            return;
        }

        try
        {
            IsBusy = true;
            var response = await server.GetDraftAsync(draftId, cancellationToken).ConfigureAwait(true);
            if (!response.TryGetProperty("viewerPayload", out var viewerPayload))
            {
                throw new InvalidDataException("Server draft has no viewerPayload.");
            }

            var serverWorkspace = StudioServerPayload.Read(viewerPayload);
            var title = response.TryGetProperty("title", out var titleValue)
                ? titleValue.GetString() ?? $"Server Draft {draftId}"
                : $"Server Draft {draftId}";
            var category = response.TryGetProperty("category", out var categoryValue) &&
                           categoryValue.TryGetProperty("slug", out var categorySlug)
                ? categorySlug.GetString() ?? "training"
                : "training";
            var package = new StudioDraftPackage(
                StudioDraftPackage.CurrentSchemaVersion,
                $"draft_{Guid.NewGuid():N}",
                $"server:{draftId}",
                title,
                serverWorkspace.PgnText,
                [$"server-draft-{draftId}.pgn"],
                serverWorkspace.GameIdentities.FirstOrDefault()?.GameId,
                serverWorkspace.GameIdentities.FirstOrDefault()?.Root.NodeId,
                serverWorkspace.TranslationLinks,
                serverWorkspace.GameIdentities,
                draftId,
                category,
                string.Empty,
                response.TryGetProperty("creditPriceMinor", out var credit) && credit.TryGetInt32(out var amount) ? amount : 0,
                DateTimeOffset.UtcNow);
            await Workspace.RestoreAsync(package, _documentLoader, cancellationToken).ConfigureAwait(true);
            ApplyDraftMetadata(package);
            await SaveLocalDraftAsync("server-resume", cancellationToken).ConfigureAwait(true);
            await RefreshAudioForCurrentNodeAsync(includeServer: true, cancellationToken).ConfigureAwait(true);
            Status = $"Draft سرور {draftId} باز و یک نسخه محلی از آن ذخیره شد.";
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Status = $"دریافت Draft سرور ناموفق بود: {exception.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task StartRecordingAsync(string scope, CancellationToken cancellationToken)
    {
        var game = Session.ActiveGame;
        var node = Session.CurrentNode;
        if (game is null || node is null || node.IsRoot || IsRecording)
        {
            return;
        }

        try
        {
            var localDraftId = DraftId ??= $"draft_{Guid.NewGuid():N}";
            var audioId = $"audio_{Guid.NewGuid():N}";
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ChessMentor",
                "MoveAudio",
                "recordings");
            var path = Path.Combine(directory, $"{audioId}.wav");
            await _audioRecorder.StartAsync(path, cancellationToken).ConfigureAwait(true);
            _recordingContext = new RecordingContext(
                audioId,
                localDraftId,
                game.Game.Id,
                game.Index,
                node.StableId,
                scope,
                Stopwatch.GetTimestamp());
            IsRecording = true;
            RecordingStatus = scope == "course"
                ? "در حال ضبط صدای عمومی مدرس…"
                : "در حال ضبط صدای شخصی…";
            _recordingClockRun?.Cancel();
            _recordingClockRun?.Dispose();
            _recordingClockRun = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            _ = UpdateRecordingClockAsync(_recordingClockRun.Token);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _audioRecorder.Cancel();
            _recordingContext = null;
            IsRecording = false;
            RecordingStatus = string.Empty;
            DesktopDiagnosticLog.Write("Studio move-audio recording start", exception);
            Status = $"شروع ضبط صدا ناموفق بود: {exception.Message}";
        }
    }

    private async Task StopRecordingAsync(CancellationToken cancellationToken)
    {
        var context = _recordingContext;
        if (context is null || !IsRecording)
        {
            return;
        }

        _recordingClockRun?.Cancel();
        var localAudioStored = false;
        try
        {
            var recording = await _audioRecorder.StopAsync(cancellationToken).ConfigureAwait(true);
            var metadata = new AudioMetadataRecord(
                context.AudioId,
                context.LocalDraftId,
                context.GameId,
                context.NodeId,
                context.Scope == "course" ? "0" : _currentServerUserId ?? _localInstallationId,
                context.Scope,
                recording.FilePath,
                null,
                recording.DurationMilliseconds,
                recording.ContentType,
                DateTimeOffset.UtcNow,
                Dirty: true);
            await _audioMetadata.UpsertAsync(metadata, cancellationToken).ConfigureAwait(true);
            localAudioStored = true;
            var warnings = new List<string>();
            try
            {
                await UploadOrQueueAudioAsync(
                    metadata,
                    context.GameIndex,
                    cancellationToken).ConfigureAwait(true);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                DesktopDiagnosticLog.Write("Studio move-audio sync queue", exception);
                warnings.Add("قرار گرفتن در صف Sync انجام نشد");
            }

            try
            {
                await SaveLocalDraftAsync("move-audio", cancellationToken).ConfigureAwait(true);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                // Audio metadata and the WAV file are already durable. A Draft
                // serialization problem must never report the recording as lost.
                DesktopDiagnosticLog.Write("Studio move-audio draft save", exception);
                warnings.Add("Autosave پیش‌نویس انجام نشد");
            }

            await RefreshAudioForCurrentNodeAsync(includeServer: false, cancellationToken).ConfigureAwait(true);
            Status = warnings.Count == 0
                ? $"صدای حرکت ذخیره شد ({FormatDuration(recording.DurationMilliseconds)})."
                : $"صدای حرکت روی دستگاه ذخیره شد ({FormatDuration(recording.DurationMilliseconds)})؛ " +
                  string.Join("؛ ", warnings) + ".";
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _audioRecorder.Cancel();
            DesktopDiagnosticLog.Write("Studio move-audio recording stop", exception);
            Status = localAudioStored
                ? $"صدا روی دستگاه ذخیره شد، ولی تکمیل عملیات ناموفق بود: {exception.Message}"
                : $"پایان ضبط صدا ناموفق بود: {exception.Message}";
        }
        finally
        {
            _recordingClockRun?.Dispose();
            _recordingClockRun = null;
            _recordingContext = null;
            RecordingStatus = string.Empty;
            IsRecording = false;
        }
    }

    private async Task UploadOrQueueAudioAsync(
        AudioMetadataRecord metadata,
        int gameIndex,
        CancellationToken cancellationToken)
    {
        var server = _server;
        if (server is not null && (ServerCourseId ?? ServerDraftId) is { } courseId &&
            IsConnected && IsAuthenticated && (metadata.Scope == "user" || IsAdmin))
        {
            try
            {
                var uploaded = await server.UploadMoveAudioAsync(
                    courseId,
                    gameIndex,
                    metadata.NodeId ?? throw new InvalidDataException("Audio node ID is missing."),
                    metadata.Scope,
                    metadata.LocalPath ?? throw new InvalidDataException("Audio file path is missing."),
                    metadata.ContentType ?? "audio/wav",
                    metadata.DurationMilliseconds,
                    cancellationToken).ConfigureAwait(true);
                await _audioMetadata.UpsertAsync(metadata with
                {
                    ServerId = uploaded.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    DurationMilliseconds = uploaded.DurationMs,
                    ContentType = uploaded.MimeType,
                    UserId = metadata.Scope == "course" ? "0" : _currentServerUserId ?? metadata.UserId,
                    UpdatedUtc = DateTimeOffset.UtcNow,
                    Dirty = false,
                }, cancellationToken).ConfigureAwait(true);
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // The durable queue below owns retry/backoff after a transient failure.
            }
        }

        var pending = new PendingMoveAudioUpload(
            metadata.CourseId ?? throw new InvalidDataException("Audio draft ID is missing."),
            metadata.Id,
            gameIndex,
            metadata.NodeId ?? throw new InvalidDataException("Audio node ID is missing."),
            metadata.Scope);
        await _syncQueue.EnqueueAsync(
            $"move-audio-upload:{metadata.Id}",
            "move-audio-upload",
            "move-audio",
            metadata.Id,
            JsonSerializer.Serialize(pending, JsonOptions),
            cancellationToken: cancellationToken).ConfigureAwait(true);
        SyncQueueCount = await _syncQueue.CountAsync(cancellationToken).ConfigureAwait(true);
    }

    private async Task UpdateRecordingClockAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(true);
                var context = _recordingContext;
                if (context is null)
                {
                    return;
                }

                var elapsed = Stopwatch.GetElapsedTime(context.StartedTimestamp);
                RecordingStatus = $"در حال ضبط · {elapsed:mm\\:ss}";
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task RefreshAudioForCurrentNodeAsync(
        bool includeServer,
        CancellationToken cancellationToken)
    {
        _audioRun?.Cancel();
        _audioRun?.Dispose();
        var run = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, cancellationToken);
        _audioRun = run;
        var localDraftId = DraftId;
        var game = Session.ActiveGame;
        var node = Session.CurrentNode;
        if (localDraftId is null || game is null || node is null || node.IsRoot)
        {
            AudioItems.Clear();
            SelectedAudio = null;
            return;
        }

        try
        {
            var server = _server;
            var courseId = ServerCourseId ?? ServerDraftId;
            var serverAudioKey = courseId is null
                ? string.Empty
                : $"{_connectedServerUrl}|{courseId.Value}|{game.Index}|{_currentServerUserId}";
            var shouldRefreshServer = includeServer ||
                !string.Equals(_serverAudioLoadedKey, serverAudioKey, StringComparison.Ordinal);
            if (shouldRefreshServer && server is not null && courseId is { } remoteCourseId &&
                IsConnected && IsAuthenticated)
            {
                try
                {
                    var remote = await server.ListMoveAudioAsync(
                        remoteCourseId,
                        game.Index,
                        run.Token).ConfigureAwait(true);
                    foreach (var item in remote)
                    {
                        var serverId = item.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
                        var existing = await _audioMetadata.FindByServerIdAsync(
                            localDraftId,
                            serverId,
                            run.Token).ConfigureAwait(true);
                        await _audioMetadata.UpsertAsync(new AudioMetadataRecord(
                            existing?.Id ?? $"server:{item.Id}",
                            localDraftId,
                            game.Game.Id,
                            item.NodeId,
                            item.Scope == "course"
                                ? "0"
                                : item.IsMine
                                    ? _currentServerUserId ?? existing?.UserId ?? _localInstallationId
                                    : existing?.UserId,
                            item.Scope,
                            existing?.LocalPath,
                            serverId,
                            item.DurationMs,
                            item.MimeType,
                            DateTimeOffset.UtcNow,
                            Dirty: false), run.Token).ConfigureAwait(true);
                    }

                    _serverAudioLoadedKey = serverAudioKey;
                }
                catch (OperationCanceledException) when (run.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    Status = $"فهرست صدای Server در دسترس نیست؛ Cache محلی فعال است: {exception.Message}";
                }
            }

            var records = await _audioMetadata.ListForNodeAsync(
                localDraftId,
                game.Game.Id,
                node.StableId,
                run.Token).ConfigureAwait(true);
            var selectedId = SelectedAudio?.Metadata.Id;
            AudioItems.Clear();
            foreach (var record in records)
            {
                AudioItems.Add(new StudioAudioItem(record));
            }

            SelectedAudio = AudioItems.FirstOrDefault(item =>
                string.Equals(item.Metadata.Id, selectedId, StringComparison.Ordinal)) ?? AudioItems.FirstOrDefault();
        }
        catch (OperationCanceledException) when (run.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Status = $"خواندن Audio Cache ناموفق بود: {exception.Message}";
        }
    }

    private async Task PlaySelectedAudioAsync(CancellationToken cancellationToken)
    {
        var selected = SelectedAudio;
        if (selected is null)
        {
            return;
        }

        try
        {
            if (string.Equals(_openedAudioId, selected.Metadata.Id, StringComparison.Ordinal))
            {
                _audioPlayer.Toggle();
                return;
            }

            var metadata = selected.Metadata;
            var path = metadata.LocalPath;
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                var server = _server ?? throw new InvalidOperationException("برای دریافت این صدا اتصال Server لازم است.");
                if (!long.TryParse(metadata.ServerId, out var serverAudioId) || serverAudioId <= 0)
                {
                    throw new InvalidDataException("شناسه Server برای این صدا موجود نیست.");
                }

                var bytes = await server.DownloadMoveAudioAsync(serverAudioId, cancellationToken).ConfigureAwait(true);
                var extension = AudioExtension(metadata.ContentType);
                var directory = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "ChessMentor",
                    "MoveAudio",
                    "cache");
                Directory.CreateDirectory(directory);
                path = Path.Combine(directory, $"server-{serverAudioId}{extension}");
                await File.WriteAllBytesAsync(path, bytes, cancellationToken).ConfigureAwait(true);
                metadata = metadata with { LocalPath = path, UpdatedUtc = DateTimeOffset.UtcNow };
                await _audioMetadata.UpsertAsync(metadata, cancellationToken).ConfigureAwait(true);
            }

            var playablePath = path ?? throw new InvalidDataException("مسیر فایل صدا موجود نیست.");
            await _audioPlayer.OpenAsync(playablePath, autoplay: true, cancellationToken).ConfigureAwait(true);
            _openedAudioId = metadata.Id;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Status = $"پخش صدا ناموفق بود: {exception.Message}";
        }
    }

    public async Task DeleteSelectedAudioAsync(CancellationToken cancellationToken = default)
    {
        var selected = SelectedAudio;
        if (selected is null)
        {
            return;
        }

        if (!CanDeleteSelectedAudio)
        {
            Status = "حذف صدای عمومی فقط برای مدیر مجاز است.";
            return;
        }

        try
        {
            var metadata = selected.Metadata;
            if (long.TryParse(metadata.ServerId, out var serverAudioId) && serverAudioId > 0)
            {
                var server = _server;
                if (server is not null && IsConnected && IsAuthenticated)
                {
                    try
                    {
                        await server.DeleteMoveAudioAsync(serverAudioId, cancellationToken).ConfigureAwait(true);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch
                    {
                        await QueueAudioDeleteAsync(serverAudioId, cancellationToken).ConfigureAwait(true);
                    }
                }
                else
                {
                    await QueueAudioDeleteAsync(serverAudioId, cancellationToken).ConfigureAwait(true);
                }
            }

            _audioPlayer.Stop();
            _openedAudioId = null;
            var localPath = metadata.LocalPath;
            if (!string.IsNullOrWhiteSpace(localPath))
            {
                await Task.Run(() => File.Delete(localPath), cancellationToken).ConfigureAwait(true);
            }

            await _syncQueue.CompleteAsync($"move-audio-upload:{metadata.Id}", cancellationToken).ConfigureAwait(true);
            await _audioMetadata.DeleteAsync(metadata.Id, cancellationToken).ConfigureAwait(true);
            await RefreshAudioForCurrentNodeAsync(includeServer: false, cancellationToken).ConfigureAwait(true);
            SyncQueueCount = await _syncQueue.CountAsync(cancellationToken).ConfigureAwait(true);
            Status = "صدای حرکت حذف شد.";
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Status = $"حذف صدا ناموفق بود: {exception.Message}";
        }
    }

    private async Task QueueAudioDeleteAsync(long serverAudioId, CancellationToken cancellationToken)
    {
        var pending = new PendingMoveAudioDelete(serverAudioId);
        await _syncQueue.EnqueueAsync(
            $"move-audio-delete:{serverAudioId}",
            "move-audio-delete",
            "move-audio",
            serverAudioId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            JsonSerializer.Serialize(pending, JsonOptions),
            cancellationToken: cancellationToken).ConfigureAwait(true);
    }

    private void EnsureServerClient()
    {
        if (!Uri.TryCreate(ServerBaseUrl.Trim(), UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https"))
        {
            throw new InvalidOperationException("آدرس Server باید یک URL معتبر http/https باشد.");
        }

        var normalized = uri.AbsoluteUri;
        if (_server is not null && string.Equals(_connectedServerUrl, normalized, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _server?.Dispose();
        _server = ServerClientFactory.Create(uri);
        _connectedServerUrl = normalized;
        _serverAudioLoadedKey = null;
        IsConnected = false;
        IsAuthenticated = false;
        IsAdmin = false;
        _currentServerUserId = null;
    }

    private void ApplyUser(JsonElement user)
    {
        IsAdmin = user.ValueKind == JsonValueKind.Object &&
                  user.TryGetProperty("role", out var role) &&
                  string.Equals(role.GetString(), "admin", StringComparison.OrdinalIgnoreCase);
        _currentServerUserId = user.ValueKind == JsonValueKind.Object &&
                               user.TryGetProperty("id", out var id)
            ? id.ValueKind switch
            {
                JsonValueKind.Number when id.TryGetInt64(out var number) =>
                    number.ToString(System.Globalization.CultureInfo.InvariantCulture),
                JsonValueKind.String => id.GetString(),
                _ => null,
            }
            : null;
        _serverAudioLoadedKey = null;
    }

    private async Task LoadCategoriesAsync(CancellationToken cancellationToken)
    {
        var server = _server;
        if (server is null)
        {
            return;
        }

        var categories = await server.GetCategoriesAsync(cancellationToken).ConfigureAwait(true);
        Categories.Clear();
        foreach (var category in categories)
        {
            Categories.Add(category);
        }

        if (Categories.Count > 0 && !Categories.Any(item =>
                string.Equals(item.Slug, CategorySlug, StringComparison.OrdinalIgnoreCase)))
        {
            CategorySlug = Categories[0].Slug;
        }
    }

    private async Task FlushSyncQueueAsync(CancellationToken cancellationToken)
    {
        var server = _server;
        if (server is null || !IsConnected)
        {
            return;
        }

        var pending = (await _syncQueue.ReadyAsync(cancellationToken: cancellationToken).ConfigureAwait(true))
            .OrderBy(static item => SyncPriority(item.OperationType))
            .ThenBy(static item => item.CreatedUtc)
            .ThenBy(static item => item.Id, StringComparer.Ordinal)
            .ToArray();
        var completed = 0;
        foreach (var item in pending)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (TranslationBacklog.IsTranslationRequest(item))
                {
                    var request = TranslationBacklog.Deserialize(item);
                    var result = await new TranslationQueue(server, _translationCache).RunAsync(
                        request.Items,
                        new TranslationQueueOptions(TranslationConcurrency),
                        cancellationToken: cancellationToken).ConfigureAwait(true);
                    foreach (var translated in result.Applied)
                    {
                        _ = Workspace.ApplyTranslation(translated, refreshPresentation: false);
                    }

                    Workspace.RefreshActivePresentation();
                    if (!result.IsComplete)
                    {
                        throw new HttpRequestException(
                            result.Failures.FirstOrDefault()?.Message ?? "Pending translation is incomplete.");
                    }
                }
                else if (string.Equals(item.OperationType, "translation-memory-update", StringComparison.Ordinal))
                {
                    if (!IsAuthenticated || !IsAdmin)
                    {
                        continue;
                    }

                    var update = JsonSerializer.Deserialize<PendingTranslationMemoryUpdate>(item.PayloadJson, JsonOptions)
                        ?? throw new InvalidDataException("Pending translation update is invalid.");
                    _ = await server.UpdateTranslationMemoryAsync(
                        update.SourceHash,
                        update.SourceText,
                        update.TranslationText,
                        cancellationToken).ConfigureAwait(true);
                }
                else if (string.Equals(item.OperationType, "studio-draft-save", StringComparison.Ordinal))
                {
                    if (!IsAuthenticated || !IsAdmin)
                    {
                        continue;
                    }

                    var pendingDraft = JsonSerializer.Deserialize<PendingStudioDraftSave>(item.PayloadJson, JsonOptions)
                        ?? throw new InvalidDataException("Pending Studio draft is invalid.");
                    var pendingRequest = pendingDraft.Request;
                    if (!string.IsNullOrWhiteSpace(pendingDraft.FeaturedImagePath))
                    {
                        pendingRequest = pendingRequest with
                        {
                            FeaturedImageData = await FeaturedImageService.ToDataUriAsync(
                                pendingDraft.FeaturedImagePath,
                                cancellationToken).ConfigureAwait(true),
                        };
                    }

                    var response = await server.SaveStudioDraftAsync(
                        pendingRequest,
                        cancellationToken).ConfigureAwait(true);
                    if (!TryReadServerDraftId(response, out var serverDraftId))
                    {
                        throw new InvalidDataException("Server draft response has no valid ID.");
                    }

                    await ApplyServerDraftIdentityAsync(
                        pendingDraft.LocalDraftId,
                        serverDraftId,
                        cancellationToken).ConfigureAwait(true);
                }
                else if (string.Equals(item.OperationType, "move-audio-upload", StringComparison.Ordinal))
                {
                    if (!IsAuthenticated)
                    {
                        continue;
                    }

                    var upload = JsonSerializer.Deserialize<PendingMoveAudioUpload>(item.PayloadJson, JsonOptions)
                        ?? throw new InvalidDataException("Pending move-audio upload is invalid.");
                    if (upload.Scope == "course" && !IsAdmin)
                    {
                        continue;
                    }

                    var draft = await _draftRepository.GetAsync(
                        upload.LocalDraftId,
                        cancellationToken).ConfigureAwait(true)
                        ?? throw new InvalidDataException("Audio draft no longer exists.");
                    var package = JsonSerializer.Deserialize<StudioDraftPackage>(draft.PayloadJson, JsonOptions)
                        ?? throw new InvalidDataException("Audio draft package is invalid.");
                    var courseId = package.ServerCourseId ?? package.ServerDraftId
                        ?? throw new InvalidOperationException("Audio is waiting for its server course or draft ID.");
                    var metadata = await _audioMetadata.GetAsync(
                        upload.AudioId,
                        cancellationToken).ConfigureAwait(true)
                        ?? throw new InvalidDataException("Audio metadata no longer exists.");
                    var gameIndex = StudioAudioIdentity.ResolveGameIndex(
                        package.GameIdentities,
                        metadata.GameId,
                        upload.GameIndex,
                        package.FlatGameIdentities);
                    if (gameIndex < 0)
                    {
                        await _syncQueue.CompleteAsync(item.Id, cancellationToken).ConfigureAwait(true);
                        await _audioMetadata.DeleteAsync(metadata.Id, cancellationToken).ConfigureAwait(true);
                        DeleteLocalAudioFile(metadata.LocalPath);
                        completed++;
                        continue;
                    }

                    var uploaded = await server.UploadMoveAudioAsync(
                        courseId,
                        gameIndex,
                        upload.NodeId,
                        upload.Scope,
                        metadata.LocalPath ?? throw new FileNotFoundException("Local audio file is missing."),
                        metadata.ContentType ?? "audio/wav",
                        metadata.DurationMilliseconds,
                        cancellationToken).ConfigureAwait(true);
                    await _audioMetadata.UpsertAsync(metadata with
                    {
                        ServerId = uploaded.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        DurationMilliseconds = uploaded.DurationMs,
                        ContentType = uploaded.MimeType,
                        UserId = metadata.Scope == "course" ? "0" : _currentServerUserId ?? metadata.UserId,
                        UpdatedUtc = DateTimeOffset.UtcNow,
                        Dirty = false,
                    }, cancellationToken).ConfigureAwait(true);
                }
                else if (string.Equals(item.OperationType, "move-audio-delete", StringComparison.Ordinal))
                {
                    if (!IsAuthenticated)
                    {
                        continue;
                    }

                    var delete = JsonSerializer.Deserialize<PendingMoveAudioDelete>(item.PayloadJson, JsonOptions)
                        ?? throw new InvalidDataException("Pending move-audio delete is invalid.");
                    await server.DeleteMoveAudioAsync(delete.ServerAudioId, cancellationToken).ConfigureAwait(true);
                }
                else
                {
                    continue;
                }

                await _syncQueue.CompleteAsync(item.Id, cancellationToken).ConfigureAwait(true);
                completed++;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                var exponent = Math.Min(item.Attempts, 8);
                var delay = TimeSpan.FromMinutes(Math.Pow(2, exponent));
                await _syncQueue.FailAsync(item.Id, exception.Message, delay, cancellationToken).ConfigureAwait(true);
            }
        }

        SyncQueueCount = await _syncQueue.CountAsync(cancellationToken).ConfigureAwait(true);
        if (completed > 0)
        {
            await ReloadDraftsAsync(cancellationToken).ConfigureAwait(true);
            await RefreshAudioForCurrentNodeAsync(includeServer: true, cancellationToken).ConfigureAwait(true);
            Status = $"{completed} عملیات آفلاین با Server همگام شد.";
        }
    }

    private bool CanUseAdminServer() =>
        _server is not null && IsConnected && IsAuthenticated && IsAdmin && Session.HasGames && !TranslationInProgress;

    private bool CanLoadServerDraft() =>
        _server is not null && IsConnected && IsAuthenticated && IsAdmin && !TranslationInProgress;

    private bool CanRecordCourseAudio() =>
        IsAdmin && !IsRecording && Session.CurrentNode is { IsRoot: false };

    private bool CanRecordUserAudio() =>
        !IsRecording && Session.CurrentNode is { IsRoot: false };

    private static int SyncPriority(string operationType) => operationType switch
    {
        "studio-draft-save" => 0,
        "translation-memory-update" => 1,
        "translation-request" => 2,
        "move-audio-upload" => 3,
        "move-audio-delete" => 4,
        _ => 10,
    };

    private static void DeleteLocalAudioFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

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

    private static bool TryReadPublishedCourseId(JsonElement response, out long courseId)
    {
        courseId = 0;
        return response.TryGetProperty("course", out var course) &&
               course.ValueKind == JsonValueKind.Object &&
               course.TryGetProperty("id", out var id) &&
               id.TryGetInt64(out courseId) &&
               courseId > 0;
    }

    private static bool TryReadServerDraftId(JsonElement response, out long draftId)
    {
        draftId = 0;
        return response.TryGetProperty("draft", out var draft) &&
               draft.ValueKind == JsonValueKind.Object &&
               draft.TryGetProperty("id", out var id) &&
               id.TryGetInt64(out draftId) &&
               draftId > 0;
    }

    private async Task ApplyServerDraftIdentityAsync(
        string localDraftId,
        long serverDraftId,
        CancellationToken cancellationToken)
    {
        var record = await _draftRepository.GetAsync(localDraftId, cancellationToken).ConfigureAwait(true);
        if (record is null)
        {
            return;
        }

        var package = JsonSerializer.Deserialize<StudioDraftPackage>(record.PayloadJson, JsonOptions)
            ?? throw new InvalidDataException("Synced local Studio draft is invalid.");
        var updated = package with
        {
            ServerDraftId = serverDraftId,
            ServerCourseId = null,
            UpdatedUtc = DateTimeOffset.UtcNow,
        };
        await _draftRepository.SaveAsync(
            localDraftId,
            updated.SourceId,
            updated.Title,
            JsonSerializer.Serialize(updated, JsonOptions),
            "server-sync",
            dirty: false,
            serverRevision: serverDraftId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            cancellationToken).ConfigureAwait(true);
        if (string.Equals(DraftId, localDraftId, StringComparison.Ordinal))
        {
            ServerDraftId = serverDraftId;
            ServerCourseId = null;
        }
    }

    private async Task ReloadDraftsAsync(CancellationToken cancellationToken)
    {
        var drafts = await _draftRepository.ListSummariesAsync(cancellationToken).ConfigureAwait(true);
        var selectedId = SelectedLocalDraft?.Id ?? DraftId;
        LocalDrafts.Clear();
        foreach (var draft in drafts)
        {
            LocalDrafts.Add(draft);
        }

        SelectedLocalDraft = LocalDrafts.FirstOrDefault(draft =>
            string.Equals(draft.Id, selectedId, StringComparison.Ordinal));
    }

    private void ScheduleAutosave()
    {
        if (_suppressAutosave || !Session.HasGames || TranslationInProgress)
        {
            return;
        }

        _autosaveRun?.Cancel();
        _autosaveRun?.Dispose();
        _autosaveRun = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _ = AutosaveAfterDelayAsync(_autosaveRun.Token);
    }

    private void MarkMetadataChanged()
    {
        if (_suppressAutosave)
        {
            return;
        }

        _metadataDirty = true;
        ScheduleAutosave();
    }

    private void ApplyDraftMetadata(StudioDraftPackage package)
    {
        _suppressAutosave = true;
        try
        {
            DraftId = package.DraftId;
            DraftTitle = package.Title;
            ServerDraftId = package.ServerDraftId;
            ServerCourseId = package.ServerCourseId;
            CategorySlug = package.CategorySlug;
            PublishSlug = package.PublishSlug;
            CreditPriceMinor = package.CreditPriceMinor;
            FeaturedImagePath = package.FeaturedImagePath ?? string.Empty;
            FeaturedImageName = package.FeaturedImageName ?? string.Empty;
            _metadataDirty = false;
        }
        finally
        {
            _suppressAutosave = false;
        }
    }

    private async Task AutosaveAfterDelayAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(900), cancellationToken).ConfigureAwait(true);
            await SaveLocalDraftAsync("autosave", cancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Status = $"Autosave ناموفق بود: {exception.Message}";
        }
    }

    private void ScheduleSettingsSave()
    {
        if (_disposed)
        {
            return;
        }

        _settingsRun?.Cancel();
        _settingsRun?.Dispose();
        _settingsRun = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _ = SaveSettingsAfterDelayAsync(_settingsRun.Token);
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
                    MoveSoundEnabled = MoveSoundEnabled,
                    HeaderCollapsed = HeaderCollapsed,
                    ViewerNotationMode = _notationMode.ToString(),
                    CommentFontSize = CommentFontSize,
                    CommentFontFamilyName = _settings.CommentFontFamilyName,
                    CustomCommentFontPath = _settings.CustomCommentFontPath,
                    TranslationConcurrency = TranslationConcurrency,
                    ServerBaseUrl = ServerBaseUrl,
                    StudioMovesPanelWidth = StudioMovesPanelWidth,
                    StudioGamesPanelWidth = StudioGamesPanelWidth,
                },
                cancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Status = $"ذخیره تنظیمات Studio ناموفق بود: {exception.Message}";
        }
    }

    private void OnWorkspaceChanged(object? sender, EventArgs eventArgs)
    {
        OnPropertyChanged(nameof(CurrentFileName));
        OnPropertyChanged(nameof(CurrentDraftLabel));
        RefreshMetrics();
        RaiseCommandStates();
        if (Workspace.IsDirty && !TranslationInProgress)
        {
            ScheduleAutosave();
        }
    }

    private void OnSettingsUpdated(AppSettings settings)
    {
        _settings = settings;
        _selectedSkin = settings.BoardSkin;
        _showCoordinates = settings.ShowCoordinates;
        _moveSoundEnabled = settings.MoveSoundEnabled;
        if (!_moveSoundEnabled)
        {
            _moveSound.Stop();
        }
        _headerCollapsed = settings.HeaderCollapsed;
        _commentFontSize = AllowedCommentFontSizes.Contains(settings.CommentFontSize)
            ? settings.CommentFontSize
            : 14;
        _commentFontFamily = CommentFontService.Resolve(
            settings.CommentFontFamilyName,
            settings.CustomCommentFontPath);
        _notationMode = Enum.TryParse<ViewerNotationMode>(
            settings.ViewerNotationMode,
            ignoreCase: true,
            out var notationMode)
            ? notationMode
            : ViewerNotationMode.Letters;
        _translationConcurrency = Math.Clamp(settings.TranslationConcurrency, 1, 12);
        _serverBaseUrl = settings.ServerBaseUrl;
        _studioMovesPanelWidth = Math.Clamp(settings.StudioMovesPanelWidth, 300, 620);
        _studioGamesPanelWidth = Math.Clamp(settings.StudioGamesPanelWidth, 230, 520);
        ApplyNotationMode();
        foreach (var property in new[]
                 {
                     nameof(SelectedSkin), nameof(ShowCoordinates), nameof(MoveSoundEnabled),
                     nameof(HeaderCollapsed), nameof(CommentFontSize), nameof(CommentFontFamily),
                     nameof(CommentFontLabel), nameof(SelectedBuiltInCommentFontFamily),
                     nameof(SelectedNotationMode),
                     nameof(TranslationConcurrency), nameof(ServerBaseUrl),
                     nameof(StudioMovesPanelWidth), nameof(StudioGamesPanelWidth),
                     nameof(StudioMovesColumnWidth), nameof(StudioGamesColumnWidth),
                 })
        {
            OnPropertyChanged(property);
        }
    }

    private void OnSessionPropertyChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        switch (eventArgs.PropertyName)
        {
            case nameof(ViewerSession.ActiveGame):
                _selectedGame = Session.ActiveGame;
                OnPropertyChanged(nameof(SelectedGame));
                OnPropertyChanged(nameof(MoveItems));
                OnPropertyChanged(nameof(MoveRows));
                OnPropertyChanged(nameof(GameHeaders));
                OnPropertyChanged(nameof(TopPlayerName));
                OnPropertyChanged(nameof(BottomPlayerName));
                break;
            case nameof(ViewerSession.CurrentNode):
                OnPropertyChanged(nameof(BoardFen));
                OnPropertyChanged(nameof(CurrentMoveLabel));
                LoadCommentEditors();
                UpdateBoardOverlay();
                LegalMoves = Array.Empty<LegalMove>();
                _ = RefreshLegalMovesAsync();
                _ = RefreshAudioForCurrentNodeAsync(includeServer: false, _lifetime.Token);
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

    private void OnGamesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs eventArgs)
    {
        if (eventArgs.NewItems is not null)
        {
            foreach (var move in eventArgs.NewItems.OfType<ViewerGameItem>()
                         .SelectMany(static game => game.MoveItems))
            {
                move.SetNotationMode(_notationMode);
            }
        }

        OnPropertyChanged(nameof(GameCount));
        OnPropertyChanged(nameof(NodeCount));
        RefreshMetrics();
        RaiseCommandStates();
    }

    private void LoadCommentEditors()
    {
        var node = Session.CurrentNode;
        _startingCommentEditor = node is null ? string.Empty : PgnTreeEditor.StartingCommentText(node);
        _commentEditor = node is null ? string.Empty : PgnTreeEditor.CommentText(node);
        OnPropertyChanged(nameof(StartingCommentEditor));
        OnPropertyChanged(nameof(CommentEditor));
    }

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

    private void RefreshMetrics()
    {
        Metrics.GameCount = GameCount;
        Metrics.NodeCount = NodeCount;
        Metrics.RefreshMemory();
        OnPropertyChanged(nameof(GameCount));
        OnPropertyChanged(nameof(NodeCount));
    }

    private void ApplyNotationMode()
    {
        foreach (var move in Games.SelectMany(static game => game.MoveItems))
        {
            move.SetNotationMode(_notationMode);
        }
    }

    private void RaiseCommandStates()
    {
        PreviousGameCommand.RaiseCanExecuteChanged();
        PreviousMoveCommand.RaiseCanExecuteChanged();
        NextMoveCommand.RaiseCanExecuteChanged();
        NextGameCommand.RaiseCanExecuteChanged();
        ApplyCommentsCommand.RaiseCanExecuteChanged();
        PromoteBranchCommand.RaiseCanExecuteChanged();
        SaveLocalDraftCommand.RaiseCanExecuteChanged();
        ResumeLocalDraftCommand.RaiseCanExecuteChanged();
        TranslateCommand.RaiseCanExecuteChanged();
        CancelTranslationCommand.RaiseCanExecuteChanged();
        SaveServerDraftCommand.RaiseCanExecuteChanged();
        PublishCommand.RaiseCanExecuteChanged();
        LoadServerDraftCommand.RaiseCanExecuteChanged();
        RaiseAudioCommandStates();
    }

    private void RaiseAudioCommandStates()
    {
        OnPropertyChanged(nameof(CanDeleteSelectedAudio));
        RecordCourseAudioCommand.RaiseCanExecuteChanged();
        RecordUserAudioCommand.RaiseCanExecuteChanged();
        StopRecordingCommand.RaiseCanExecuteChanged();
        PlayAudioCommand.RaiseCanExecuteChanged();
        DeleteAudioCommand.RaiseCanExecuteChanged();
    }

    private static string Slugify(string value)
    {
        var builder = new System.Text.StringBuilder();
        var hyphenPending = false;
        foreach (var character in (value ?? string.Empty).Normalize().Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(character) || character == '_')
            {
                if (hyphenPending && builder.Length > 0)
                {
                    builder.Append('-');
                }

                builder.Append(character);
                hyphenPending = false;
            }
            else
            {
                hyphenPending = builder.Length > 0;
            }

            if (builder.Length >= 120)
            {
                break;
            }
        }

        return builder.ToString().Trim('-');
    }

    private void OnAudioPlaybackStateChanged(object? sender, MoveAudioPlaybackState state)
    {
        IsAudioPlaying = state.IsPlaying;
        _updatingAudioPosition = true;
        try
        {
            AudioDurationMilliseconds = state.DurationMilliseconds > 0
                ? state.DurationMilliseconds
                : SelectedAudio?.Metadata.DurationMilliseconds ?? 0;
            AudioPositionMilliseconds = state.PositionMilliseconds;
        }
        finally
        {
            _updatingAudioPosition = false;
        }

        OnPropertyChanged(nameof(AudioTimeLabel));
        if (!string.IsNullOrWhiteSpace(state.Error))
        {
            Status = $"پخش صدا ناموفق بود: {state.Error}";
        }
    }

    private static string FormatDuration(long milliseconds)
    {
        var duration = TimeSpan.FromMilliseconds(Math.Max(0, milliseconds));
        return duration.TotalHours >= 1
            ? duration.ToString("h\\:mm\\:ss", System.Globalization.CultureInfo.InvariantCulture)
            : duration.ToString("mm\\:ss", System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string AudioExtension(string? contentType) => contentType?.Split(';')[0].Trim().ToLowerInvariant() switch
    {
        "audio/webm" or "video/webm" => ".webm",
        "audio/ogg" or "application/ogg" => ".ogg",
        "audio/mp4" or "video/mp4" or "audio/x-m4a" => ".m4a",
        "audio/mpeg" or "audio/mp3" => ".mp3",
        _ => ".wav",
    };

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _settingsRepository.Updated -= OnSettingsUpdated;
        Workspace.Changed -= OnWorkspaceChanged;
        Session.PropertyChanged -= OnSessionPropertyChanged;
        Session.Games.CollectionChanged -= OnGamesCollectionChanged;
        _lifetime.Cancel();
        TranslateCommand.Cancel();
        _loadRun?.Cancel();
        _legalRun?.Cancel();
        _candidateRun?.Cancel();
        _autosaveRun?.Cancel();
        _settingsRun?.Cancel();
        _audioRun?.Cancel();
        _recordingClockRun?.Cancel();
        _loadRun?.Dispose();
        _legalRun?.Dispose();
        _candidateRun?.Dispose();
        _autosaveRun?.Dispose();
        _settingsRun?.Dispose();
        _audioRun?.Dispose();
        _recordingClockRun?.Dispose();
        TranslateCommand.Dispose();
        ApplyCommentsCommand.Dispose();
        SaveLocalDraftCommand.Dispose();
        ResumeLocalDraftCommand.Dispose();
        ConnectCommand.Dispose();
        SaveServerDraftCommand.Dispose();
        PublishCommand.Dispose();
        LoadServerDraftCommand.Dispose();
        RecordCourseAudioCommand.Dispose();
        RecordUserAudioCommand.Dispose();
        StopRecordingCommand.Dispose();
        PlayAudioCommand.Dispose();
        DeleteAudioCommand.Dispose();
        SearchTranslationCacheCommand.Dispose();
        ApplyTranslationCacheEditCommand.Dispose();
        _server?.Dispose();
        _moveSound.Dispose();
        _audioPlayer.StateChanged -= OnAudioPlaybackStateChanged;
        _audioPlayer.Dispose();
        _audioRecorder.Dispose();
        _lifetime.Dispose();
    }

    private sealed record PendingTranslationMemoryUpdate(
        string SourceHash,
        string SourceText,
        string TranslationText);

    private sealed record PendingStudioDraftSave(
        string LocalDraftId,
        StudioDraftRequest Request,
        string? FeaturedImagePath = null);

    private sealed record PendingMoveAudioUpload(
        string LocalDraftId,
        string AudioId,
        int GameIndex,
        string NodeId,
        string Scope);

    private sealed record PendingMoveAudioDelete(long ServerAudioId);

    private sealed record RecordingContext(
        string AudioId,
        string LocalDraftId,
        string GameId,
        int GameIndex,
        string NodeId,
        string Scope,
        long StartedTimestamp);
}

public sealed record StudioAudioItem(AudioMetadataRecord Metadata)
{
    public string Label =>
        $"{(Metadata.Scope == "course" ? "مدرس" : "شخصی")} · " +
        $"{TimeSpan.FromMilliseconds(Metadata.DurationMilliseconds):mm\\:ss}" +
        (Metadata.Dirty ? " · در صف Sync" : string.Empty);
}
