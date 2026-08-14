using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using ChessMentor.Chess;
using ChessMentor.Core.Mvvm;
using ChessMentor.Desktop.Controls;
using ChessMentor.MoveTrainer;
using ChessMentor.Persistence;
using ChessMentor.Viewer;

namespace ChessMentor.Desktop.ViewModels;

public sealed class MoveTrainerWindowViewModel : ObservableObject, IDisposable
{
    private const string OfflineUserId = "local-user";
    private readonly AppDatabase _database;
    private readonly SettingsRepository _settingsRepository;
    private readonly ViewerDocumentLoader _loader;
    private readonly ManagedChessRules _rules;
    private readonly MoveTrainerRepository _repository;
    private readonly MoveTrainerCourseFactory _factory = new();
    private readonly TrainerAnswerEvaluator _evaluator;
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _legalRun;
    private TrainerCourse? _course;
    private MoveTrainerSession? _session;
    private TrainerSessionSnapshot? _resumableSession;
    private TrainerItemEditor? _selectedEditor;
    private string _courseTitle = "دوره تمرینی بدون عنوان";
    private string _boardFen = FenPosition.Initial;
    private BoardOrientation _orientation = BoardOrientation.White;
    private BoardSkin _selectedSkin = BoardSkin.Chessmentor;
    private bool _showCoordinates = true;
    private IReadOnlyList<LegalMove> _legalMoves = Array.Empty<LegalMove>();
    private BoardOverlay _boardOverlay = new();
    private bool _isBusy;
    private bool _isTraining;
    private string _status = "یک یا چند فایل PGN باز کنید، یا یک دوره ذخیره‌شده را انتخاب کنید.";
    private string _prompt = string.Empty;
    private string _feedback = string.Empty;
    private string _hint = string.Empty;
    private string _progress = "۰ / ۰";
    private string _stats = "هنوز تلاشی ثبت نشده است.";
    private TrainerSide _selectedSide = TrainerSide.Both;
    private TrainerScheduleMode _selectedScheduleMode = TrainerScheduleMode.Spaced;
    private bool _acceptTranspositions = true;
    private bool _allowRetry = true;
    private int _dailyNewLimit = 20;
    private int _dailyReviewLimit = 100;
    private int _maxSessionItems = 50;
    private int _customIntervalDays = 1;
    private int _cyclicalRepetitions = 1;
    private int _hintsUsed;
    private long _itemStartedTimestamp;
    private bool _disposed;

    public MoveTrainerWindowViewModel(
        AppDatabase database,
        SettingsRepository settingsRepository,
        ViewerDocumentLoader loader,
        ManagedChessRules rules)
    {
        _database = database;
        _settingsRepository = settingsRepository;
        _loader = loader;
        _rules = rules;
        _repository = new MoveTrainerRepository(database);
        _evaluator = new TrainerAnswerEvaluator(rules);
        _settingsRepository.Updated += OnSettingsUpdated;
        SaveCourseCommand = new AsyncRelayCommand(SaveCourseCommandAsync, CanSaveCourse);
        StartTrainingCommand = new AsyncRelayCommand(StartTrainingAsync, CanStartTraining);
        ResumeTrainingCommand = new RelayCommand(ResumeTraining, CanResumeTraining);
        ShowHintCommand = new RelayCommand(ShowNextHint, CanShowHint);
        RetryMistakesCommand = new AsyncRelayCommand(RetryMistakesAsync, CanRetryMistakes);
        StopTrainingCommand = new RelayCommand(StopTraining, () => IsTraining && !IsBusy);
        FlipBoardCommand = new RelayCommand(() => Orientation = Orientation == BoardOrientation.White
            ? BoardOrientation.Black
            : BoardOrientation.White);
    }

    public ObservableCollection<MoveTrainerCourseListItem> Courses { get; } = new();
    public ObservableCollection<TrainerItemEditor> Items { get; } = new();
    public IReadOnlyList<TrainerSide> TrainerSides { get; } = Enum.GetValues<TrainerSide>();
    public IReadOnlyList<TrainerScheduleMode> ScheduleModes { get; } = Enum.GetValues<TrainerScheduleMode>();
    public AsyncRelayCommand SaveCourseCommand { get; }
    public AsyncRelayCommand StartTrainingCommand { get; }
    public RelayCommand ResumeTrainingCommand { get; }
    public RelayCommand ShowHintCommand { get; }
    public AsyncRelayCommand RetryMistakesCommand { get; }
    public RelayCommand StopTrainingCommand { get; }
    public RelayCommand FlipBoardCommand { get; }

    public string CourseTitle
    {
        get => _courseTitle;
        set
        {
            if (SetProperty(ref _courseTitle, value))
            {
                UpdateCommandStates();
            }
        }
    }

    public TrainerItemEditor? SelectedEditor
    {
        get => _selectedEditor;
        set
        {
            if (!SetProperty(ref _selectedEditor, value) || value is null || IsTraining)
            {
                return;
            }

            ShowItem(value.Item);
        }
    }

    public string BoardFen
    {
        get => _boardFen;
        private set => SetProperty(ref _boardFen, value);
    }

    public BoardOrientation Orientation
    {
        get => _orientation;
        private set => SetProperty(ref _orientation, value);
    }

    public BoardSkin SelectedSkin
    {
        get => _selectedSkin;
        private set => SetProperty(ref _selectedSkin, value);
    }

    public bool ShowCoordinates
    {
        get => _showCoordinates;
        private set => SetProperty(ref _showCoordinates, value);
    }

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

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                UpdateCommandStates();
            }
        }
    }

    public bool IsTraining
    {
        get => _isTraining;
        private set
        {
            if (SetProperty(ref _isTraining, value))
            {
                OnPropertyChanged(nameof(IsAuthoring));
                UpdateCommandStates();
            }
        }
    }

    public bool IsAuthoring => !IsTraining;
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public string Prompt { get => _prompt; private set => SetProperty(ref _prompt, value); }
    public string Feedback { get => _feedback; private set => SetProperty(ref _feedback, value); }
    public string Hint { get => _hint; private set => SetProperty(ref _hint, value); }
    public string Progress { get => _progress; private set => SetProperty(ref _progress, value); }
    public string Stats { get => _stats; private set => SetProperty(ref _stats, value); }
    public TrainerSide SelectedSide { get => _selectedSide; set => SetProperty(ref _selectedSide, value); }
    public TrainerScheduleMode SelectedScheduleMode
    {
        get => _selectedScheduleMode;
        set
        {
            if (SetProperty(ref _selectedScheduleMode, value))
            {
                OnPropertyChanged(nameof(UsesCustomInterval));
                OnPropertyChanged(nameof(UsesCyclicalRepetitions));
            }
        }
    }

    public bool AcceptTranspositions { get => _acceptTranspositions; set => SetProperty(ref _acceptTranspositions, value); }
    public bool AllowRetry { get => _allowRetry; set => SetProperty(ref _allowRetry, value); }
    public int DailyNewLimit { get => _dailyNewLimit; set => SetProperty(ref _dailyNewLimit, Math.Clamp(value, 0, 500)); }
    public int DailyReviewLimit { get => _dailyReviewLimit; set => SetProperty(ref _dailyReviewLimit, Math.Clamp(value, 0, 2000)); }
    public int MaxSessionItems { get => _maxSessionItems; set => SetProperty(ref _maxSessionItems, Math.Clamp(value, 1, 500)); }
    public int CustomIntervalDays { get => _customIntervalDays; set => SetProperty(ref _customIntervalDays, Math.Clamp(value, 1, 3650)); }
    public int CyclicalRepetitions { get => _cyclicalRepetitions; set => SetProperty(ref _cyclicalRepetitions, Math.Clamp(value, 1, 3650)); }
    public bool UsesCustomInterval => SelectedScheduleMode == TrainerScheduleMode.Custom;
    public bool UsesCyclicalRepetitions => SelectedScheduleMode == TrainerScheduleMode.Cyclical;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken = EffectiveToken(cancellationToken);
        await _database.InitializeAsync(cancellationToken).ConfigureAwait(true);
        var settings = await _settingsRepository.LoadAsync(cancellationToken).ConfigureAwait(true);
        ApplySettings(settings);
        await ReloadCoursesAsync(cancellationToken).ConfigureAwait(true);
    }

    public async Task LoadPgnFilesAsync(
        IReadOnlyList<string> filePaths,
        CancellationToken cancellationToken = default)
    {
        cancellationToken = EffectiveToken(cancellationToken);
        if (filePaths.Count == 0 || IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var loaded = await _loader.LoadAsync(filePaths, cancellationToken).ConfigureAwait(true);
            if (loaded.Sources.Count == 0)
            {
                Status = loaded.Diagnostics.FirstOrDefault() ?? "هیچ بازی معتبری در PGN پیدا نشد.";
                return;
            }

            var title = filePaths.Count == 1
                ? Path.GetFileNameWithoutExtension(filePaths[0])
                : $"دوره {loaded.GameCount} بازی";
            var sourcePgn = string.Join(
                Environment.NewLine + Environment.NewLine,
                loaded.Sources.Select(static source => source.Document.Serialize()));
            var candidate = await Task.Run(
                () => _factory.CreateCandidateCourse(
                    title,
                    loaded.Sources.Select(static source => source.Document),
                    sourcePgn),
                cancellationToken).ConfigureAwait(true);
            SetCourse(candidate);
            Status = $"{loaded.GameCount:N0} بازی و {candidate.Items.Count:N0} موقعیت تمرینی آمادهٔ ویرایش است.";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Status = $"ساخت دوره از PGN ناموفق بود: {exception.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task LoadCourseAsync(
        MoveTrainerCourseListItem entry,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        cancellationToken = EffectiveToken(cancellationToken);
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var course = await _repository.GetCourseAsync(entry.Id, cancellationToken).ConfigureAwait(true)
                ?? throw new InvalidDataException("دورهٔ انتخاب‌شده در دیتابیس پیدا نشد.");
            SetCourse(course);
            await RefreshStatsAsync(cancellationToken).ConfigureAwait(true);
            var resumable = await _repository.GetLatestActiveSessionAsync(
                OfflineUserId,
                course.Id,
                cancellationToken).ConfigureAwait(true);
            _resumableSession = resumable;
            UpdateCommandStates();
            Status = resumable is null
                ? $"دوره «{course.Title}» با {course.Items.Count:N0} موقعیت باز شد."
                : $"دوره باز شد؛ یک جلسه نیمه‌تمام در موقعیت {resumable.CurrentIndex + 1:N0} قابل ادامه است.";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Status = $"بازکردن دوره ناموفق بود: {exception.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task HandleMoveAsync(
        BoardMoveRequestedEventArgs move,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(move);
        cancellationToken = EffectiveToken(cancellationToken);
        var session = _session;
        var course = _course;
        if (!IsTraining || session is null || course is null || IsBusy)
        {
            return;
        }

        var item = session.CurrentItem;
        if (item is null)
        {
            return;
        }

        var uci = move.From.Name + move.To.Name + (move.Promotion?.ToString() ?? string.Empty);
        var position = FenPosition.Parse(item.Fen);
        var piece = position[move.From]?.ToString() ?? string.Empty;
        var elapsed = Stopwatch.GetElapsedTime(_itemStartedTimestamp);
        var request = new TrainerAttemptRequest(
            uci,
            move.WasDrag ? TrainerInputMethod.Drag : TrainerInputMethod.Click,
            piece,
            move.From.Name,
            move.To.Name,
            _hintsUsed,
            (int)Math.Clamp(elapsed.TotalMilliseconds, 0, 600_000));
        try
        {
            IsBusy = true;
            var evaluation = session.Submit(request, cancellationToken);
            await _repository.RecordAttemptAsync(
                OfflineUserId,
                course,
                item,
                request,
                evaluation,
                DateTimeOffset.UtcNow,
                cancellationToken: cancellationToken).ConfigureAwait(true);
            await _repository.SaveSessionAsync(
                OfflineUserId,
                course.Id,
                session.Snapshot(),
                DateTimeOffset.UtcNow,
                cancellationToken).ConfigureAwait(true);
            Feedback = evaluation.Feedback;
            if (evaluation.IsLegal)
            {
                BoardOverlay = new BoardOverlay(
                    LastMoveFrom: move.From,
                    LastMoveTo: move.To);
            }

            if (evaluation.CompletesItem)
            {
                Status = evaluation.IsTransposition
                    ? "پاسخ از راه ترنسپوزیشن پذیرفته شد."
                    : evaluation.Outcome == TrainerOutcome.SoftFail
                        ? "حرکت پذیرفته شد، اما بهترین پاسخ نبود."
                        : "پاسخ درست بود.";
                await Task.Delay(TimeSpan.FromMilliseconds(350), cancellationToken).ConfigureAwait(true);
                ShowCurrentTrainingItem();
            }
            else
            {
                Status = evaluation.IsLegal ? "پاسخ اشتباه؛ دوباره تلاش کنید." : "حرکت غیرقانونی ثبت شد.";
                UpdateProgress();
            }

            await RefreshStatsAsync(cancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Status = $"ثبت تلاش ناموفق بود: {exception.Message}";
        }
        finally
        {
            IsBusy = false;
            UpdateCommandStates();
        }
    }

    private async Task SaveCourseCommandAsync(CancellationToken cancellationToken)
    {
        try
        {
            IsBusy = true;
            var course = BuildEditedCourse();
            await _repository.SaveCourseAsync(course, cancellationToken).ConfigureAwait(true);
            _course = course;
            await ReloadCoursesAsync(cancellationToken).ConfigureAwait(true);
            Status = $"دوره «{course.Title}» با {course.Items.Count(static item => item.Enabled):N0} آیتم فعال ذخیره شد.";
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Status = $"ذخیره دوره ناموفق بود: {exception.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task StartTrainingAsync(CancellationToken cancellationToken)
    {
        try
        {
            IsBusy = true;
            var course = BuildEditedCourse();
            await _repository.SaveCourseAsync(course, cancellationToken).ConfigureAwait(true);
            _course = course;
            var plan = await _repository.BuildQueueAsync(
                OfflineUserId,
                course,
                DateTimeOffset.UtcNow,
                cancellationToken).ConfigureAwait(true);
            if (plan.Items.Count == 0)
            {
                Status = "برای امروز آیتم جدید یا مرور سررسیدشده‌ای باقی نمانده است.";
                return;
            }

            var session = new MoveTrainerSession(
                plan.Items.Select(static candidate => candidate.Item),
                course.Settings,
                _evaluator);
            _session = session;
            _resumableSession = null;
            IsTraining = true;
            Status = $"جلسه شروع شد: {plan.ReviewCount:N0} مرور و {plan.NewCount:N0} آیتم جدید.";
            ShowCurrentTrainingItem();
            await _repository.SaveSessionAsync(
                OfflineUserId,
                course.Id,
                session.Snapshot(),
                DateTimeOffset.UtcNow,
                cancellationToken).ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Status = $"شروع تمرین ناموفق بود: {exception.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ShowNextHint()
    {
        var hints = _session?.CurrentItem?.Hints;
        if (hints is null || _hintsUsed >= hints.Count)
        {
            return;
        }

        var next = hints[_hintsUsed++];
        Hint = next.Text;
        Status = $"راهنما نمایش داده شد؛ جریمه {Math.Max(0, next.Penalty):N0} امتیاز.";
        UpdateCommandStates();
    }

    private async Task RetryMistakesAsync(CancellationToken cancellationToken)
    {
        var session = _session;
        var course = _course;
        if (session?.RetryMistakes() != true || course is null)
        {
            return;
        }

        try
        {
            await _repository.SaveSessionAsync(
                OfflineUserId,
                course.Id,
                session.Snapshot(),
                DateTimeOffset.UtcNow,
                cancellationToken).ConfigureAwait(true);
            Status = "جلسهٔ Retry Mistakes شروع شد.";
            ShowCurrentTrainingItem();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Status = $"ذخیره جلسه Retry Mistakes ناموفق بود: {exception.Message}";
        }
    }

    private void ResumeTraining()
    {
        var course = _course;
        var snapshot = _resumableSession;
        if (course is null || snapshot is null)
        {
            return;
        }

        _session = MoveTrainerSession.Restore(course.Items, course.Settings, snapshot, _evaluator);
        _resumableSession = null;
        IsTraining = true;
        Status = "جلسه نیمه‌تمام از همان موقعیت ادامه یافت.";
        ShowCurrentTrainingItem();
    }

    private void StopTraining()
    {
        var snapshot = _session?.Snapshot();
        _resumableSession = snapshot is { IsComplete: false } ? snapshot : null;
        IsTraining = false;
        _session = null;
        Prompt = string.Empty;
        Feedback = string.Empty;
        Hint = string.Empty;
        Progress = "۰ / ۰";
        if (SelectedEditor is { } editor)
        {
            ShowItem(editor.Item);
        }

        Status = "تمرین متوقف شد؛ پیشرفت ثبت‌شده حفظ شده است.";
    }

    private void ShowCurrentTrainingItem()
    {
        var item = _session?.CurrentItem;
        if (item is null)
        {
            BoardOverlay = new BoardOverlay();
            LegalMoves = Array.Empty<LegalMove>();
            Prompt = "جلسه تمام شد.";
            Hint = string.Empty;
            Feedback = string.Empty;
            UpdateProgress();
            Status = _session?.Snapshot().MistakeItemIds.Count > 0
                ? "جلسه تمام شد. می‌توانید اشتباه‌ها را دوباره تمرین کنید."
                : "جلسه بدون اشتباه باقی‌مانده تمام شد.";
            UpdateCommandStates();
            return;
        }

        _hintsUsed = 0;
        _itemStartedTimestamp = Stopwatch.GetTimestamp();
        Prompt = item.Prompt;
        Feedback = string.Empty;
        Hint = string.Empty;
        ShowItem(item);
        UpdateProgress();
    }

    private void ShowItem(TrainerItem item)
    {
        BoardFen = item.Fen;
        Orientation = TrainerOrientation.FromFen(item.Fen);
        BoardOverlay = new BoardOverlay();
        _ = RefreshLegalMovesAsync(item.Fen);
    }

    private async Task RefreshLegalMovesAsync(string fen)
    {
        _legalRun?.Cancel();
        _legalRun?.Dispose();
        var run = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _legalRun = run;
        try
        {
            var moves = await Task.Run(
                () => _rules.GetLegalMoves(fen, run.Token),
                run.Token).ConfigureAwait(true);
            if (string.Equals(BoardFen, fen, StringComparison.Ordinal))
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

    private TrainerCourse BuildEditedCourse()
    {
        var source = _course ?? throw new InvalidOperationException("ابتدا PGN یا دوره‌ای را باز کنید.");
        if (string.IsNullOrWhiteSpace(CourseTitle))
        {
            throw new InvalidDataException("عنوان دوره خالی است.");
        }

        return source with
        {
            Title = CourseTitle.Trim(),
            Items = Items.Select(static editor => editor.Build()).ToArray(),
            Settings = new TrainerCourseSettings(
                SelectedSide,
                SelectedScheduleMode,
                AcceptTranspositions,
                AllowRetry,
                DailyNewLimit,
                DailyReviewLimit,
                MaxSessionItems,
                CustomIntervalDays,
                CyclicalRepetitions).Normalize(),
            UpdatedUtc = DateTimeOffset.UtcNow,
        };
    }

    private void SetCourse(TrainerCourse course)
    {
        _course = course;
        _session = null;
        _resumableSession = null;
        IsTraining = false;
        CourseTitle = course.Title;
        var settings = course.Settings.Normalize();
        SelectedSide = settings.Side;
        SelectedScheduleMode = settings.ScheduleMode;
        AcceptTranspositions = settings.AcceptTranspositions;
        AllowRetry = settings.AllowRetry;
        DailyNewLimit = settings.DailyNewLimit;
        DailyReviewLimit = settings.DailyReviewLimit;
        MaxSessionItems = settings.MaxSessionItems;
        CustomIntervalDays = settings.CustomIntervalDays;
        CyclicalRepetitions = settings.CyclicalRepetitions;
        Items.Clear();
        foreach (var item in course.Items)
        {
            Items.Add(new TrainerItemEditor(item));
        }

        SelectedEditor = Items.FirstOrDefault();
        Prompt = string.Empty;
        Feedback = string.Empty;
        Hint = string.Empty;
        Progress = "۰ / ۰";
        Stats = "هنوز تلاشی برای این دوره ثبت نشده است.";
        UpdateCommandStates();
    }

    private async Task ReloadCoursesAsync(CancellationToken cancellationToken)
    {
        var courses = await _repository.ListCoursesAsync(cancellationToken).ConfigureAwait(true);
        Courses.Clear();
        foreach (var course in courses)
        {
            Courses.Add(new MoveTrainerCourseListItem(
                course.Id,
                course.Title,
                course.Items.Count,
                course.UpdatedUtc));
        }
    }

    private async Task RefreshStatsAsync(CancellationToken cancellationToken)
    {
        var course = _course;
        if (course is null)
        {
            return;
        }

        var value = await _repository.GetStatsAsync(
            OfflineUserId,
            course.Id,
            DateTimeOffset.UtcNow,
            cancellationToken).ConfigureAwait(true);
        Stats = $"تلاش {value.Attempts:N0} · درست {value.Correct:N0} · " +
                $"قابل‌قبول {value.SoftFails:N0} · اشتباه {value.Mistakes:N0} · " +
                $"دقت {value.Accuracy:N1}% · سررسید {value.Due:N0}";
    }

    private void UpdateProgress()
    {
        var session = _session;
        Progress = session is null
            ? "۰ / ۰"
            : $"{Math.Min(session.CurrentIndex + (session.IsComplete ? 0 : 1), session.Total):N0} / {session.Total:N0}";
    }

    private bool CanSaveCourse() => _course is not null && !IsBusy && !IsTraining;
    private bool CanStartTraining() => _course is not null && Items.Any(static item => item.Enabled) && !IsBusy && !IsTraining;
    private bool CanResumeTraining() => _course is not null && _resumableSession is not null && !IsBusy && !IsTraining;
    private bool CanShowHint() => IsTraining && _session?.CurrentItem is { } item && _hintsUsed < item.Hints.Count;
    private bool CanRetryMistakes()
    {
        var session = _session;
        return IsTraining && session is { IsComplete: true } &&
               session.Snapshot().MistakeItemIds.Count > 0;
    }

    private void UpdateCommandStates()
    {
        SaveCourseCommand.RaiseCanExecuteChanged();
        StartTrainingCommand.RaiseCanExecuteChanged();
        ResumeTrainingCommand.RaiseCanExecuteChanged();
        ShowHintCommand.RaiseCanExecuteChanged();
        RetryMistakesCommand.RaiseCanExecuteChanged();
        StopTrainingCommand.RaiseCanExecuteChanged();
    }

    private void OnSettingsUpdated(AppSettings settings) => ApplySettings(settings);

    private void ApplySettings(AppSettings settings)
    {
        SelectedSkin = settings.BoardSkin;
        ShowCoordinates = settings.ShowCoordinates;
    }

    private CancellationToken EffectiveToken(CancellationToken token) =>
        token.CanBeCanceled ? token : _lifetime.Token;

    public void ReportBoardError(string message) => Status = $"FEN نامعتبر: {message}";

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _settingsRepository.Updated -= OnSettingsUpdated;
        _legalRun?.Cancel();
        _legalRun?.Dispose();
        SaveCourseCommand.Dispose();
        StartTrainingCommand.Dispose();
        RetryMistakesCommand.Dispose();
        _lifetime.Cancel();
        _lifetime.Dispose();
    }
}

public sealed record MoveTrainerCourseListItem(
    string Id,
    string Title,
    int ItemCount,
    DateTimeOffset UpdatedUtc)
{
    public string Subtitle => $"{ItemCount:N0} موقعیت · {UpdatedUtc.LocalDateTime:yyyy/MM/dd HH:mm}";
}

public sealed class TrainerItemEditor : ObservableObject
{
    private bool _enabled;
    private int _priority;
    private string _prompt;
    private string _wrongMoveFeedback;
    private string _acceptedMovesEditor;
    private string _hintsEditor;
    private bool _answersDirty;
    private bool _hintsDirty;

    public TrainerItemEditor(TrainerItem item)
    {
        Item = item;
        _enabled = item.Enabled;
        _priority = item.Priority;
        _prompt = item.Prompt;
        _wrongMoveFeedback = item.WrongMoveFeedback;
        _acceptedMovesEditor = string.Join(
            Environment.NewLine,
            item.Answers.Select(static answer =>
                $"{answer.Uci}|{AnswerKindName(answer.Kind)}|{answer.Feedback}"));
        _hintsEditor = string.Join(
            Environment.NewLine,
            item.Hints.Select(static hint => $"{hint.Kind}|{hint.Penalty}|{hint.Text}"));
    }

    public TrainerItem Item { get; private set; }
    public string Id => Item.Id;
    public string NodeId => Item.NodeId;
    public string AnswersSummary => string.Join(
        " · ",
        AcceptedMovesEditor.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(static line => line.Split('|', 2)[0].Trim()));
    public string Label => $"{Item.GameId} · {Item.NodeId}";

    public bool Enabled { get => _enabled; set => SetProperty(ref _enabled, value); }
    public int Priority { get => _priority; set => SetProperty(ref _priority, Math.Clamp(value, 0, 100)); }
    public string Prompt { get => _prompt; set => SetProperty(ref _prompt, value ?? string.Empty); }
    public string WrongMoveFeedback { get => _wrongMoveFeedback; set => SetProperty(ref _wrongMoveFeedback, value ?? string.Empty); }
    public string AcceptedMovesEditor
    {
        get => _acceptedMovesEditor;
        set
        {
            if (SetProperty(ref _acceptedMovesEditor, value ?? string.Empty))
            {
                _answersDirty = true;
                OnPropertyChanged(nameof(AnswersSummary));
            }
        }
    }

    public string HintsEditor
    {
        get => _hintsEditor;
        set
        {
            if (SetProperty(ref _hintsEditor, value ?? string.Empty))
            {
                _hintsDirty = true;
            }
        }
    }

    public TrainerItem Build()
    {
        var answers = _answersDirty ? ParseAnswers() : Item.Answers;
        var hints = _hintsDirty ? ParseHints() : Item.Hints;
        Item = Item with
        {
            Enabled = Enabled,
            Priority = Priority,
            Prompt = Prompt.Trim(),
            WrongMoveFeedback = WrongMoveFeedback.Trim(),
            Answers = answers,
            Hints = hints,
        };
        _answersDirty = false;
        _hintsDirty = false;
        return Item;
    }

    private IReadOnlyList<TrainerAnswer> ParseAnswers()
    {
        var rules = ManagedChessRules.Instance;
        var legal = rules.GetLegalMoves(Item.Fen);
        var answers = new List<TrainerAnswer>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in AcceptedMovesEditor.Split(
                     ['\r', '\n'],
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var fields = raw.Split('|', 3, StringSplitOptions.TrimEntries);
            var token = fields[0];
            var move = legal.FirstOrDefault(candidate =>
                string.Equals(candidate.Uci, token, StringComparison.OrdinalIgnoreCase));
            if (move is null && rules.TryResolveSan(Item.Fen, token, out var resolution))
            {
                move = resolution!.Move;
            }

            if (move is null)
            {
                throw new InvalidDataException($"پاسخ «{token}» در موقعیت {Item.NodeId} قانونی نیست.");
            }

            if (!seen.Add(move.Uci))
            {
                continue;
            }

            var kind = fields.Length > 1 ? ParseAnswerKind(fields[1]) : TrainerAnswerKind.Alternate;
            var feedback = fields.Length > 2 ? fields[2] : string.Empty;
            var resultFen = rules.ApplyMove(Item.Fen, move.Uci);
            answers.Add(new TrainerAnswer(
                move.Uci,
                move.San,
                kind,
                feedback,
                ManagedChessRules.PositionKey(resultFen)));
        }

        if (answers.Count == 0)
        {
            throw new InvalidDataException($"موقعیت {Item.NodeId} حداقل یک پاسخ لازم دارد.");
        }

        if (answers.All(static answer => answer.Kind != TrainerAnswerKind.Primary))
        {
            answers[0] = answers[0] with { Kind = TrainerAnswerKind.Primary };
        }

        return answers;
    }

    private IReadOnlyList<TrainerHint> ParseHints()
    {
        var hints = new List<TrainerHint>();
        foreach (var raw in HintsEditor.Split(
                     ['\r', '\n'],
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var fields = raw.Split('|', 3, StringSplitOptions.TrimEntries);
            if (fields.Length == 1)
            {
                hints.Add(new TrainerHint("text", fields[0], 0));
                continue;
            }

            var penalty = fields.Length > 1 && int.TryParse(fields[1], out var parsed)
                ? Math.Clamp(parsed, 0, 100)
                : 0;
            var text = fields.Length > 2 ? fields[2] : fields[1];
            hints.Add(new TrainerHint(fields[0].Length == 0 ? "text" : fields[0], text, penalty));
        }

        return hints;
    }

    private static TrainerAnswerKind ParseAnswerKind(string value) =>
        value.Trim().ToLowerInvariant() switch
        {
            "primary" or "اصلی" => TrainerAnswerKind.Primary,
            "soft_fail" or "softfail" or "قابل قبول" => TrainerAnswerKind.SoftFail,
            _ => TrainerAnswerKind.Alternate,
        };

    private static string AnswerKindName(TrainerAnswerKind kind) => kind switch
    {
        TrainerAnswerKind.Primary => "primary",
        TrainerAnswerKind.SoftFail => "soft_fail",
        _ => "alternate",
    };
}
