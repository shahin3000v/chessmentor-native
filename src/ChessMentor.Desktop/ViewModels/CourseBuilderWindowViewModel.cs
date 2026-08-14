using System.Collections.ObjectModel;
using ChessMentor.Chess;
using ChessMentor.Core.Mvvm;
using ChessMentor.CourseBuilder;
using ChessMentor.Persistence;
using ChessMentor.Viewer;

namespace ChessMentor.Desktop.ViewModels;

public sealed class CourseBuilderWindowViewModel : ObservableObject, IDisposable
{
    private readonly CourseBuilderRepository _repository;
    private readonly SettingsRepository _settingsRepository;
    private readonly ViewerDocumentLoader _loader;
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _autosave;
    private CourseDocumentEditor? _editor;
    private CourseBuilderDocumentSummary? _selectedDocument;
    private CourseBlockListItem? _selectedBlock;
    private string _courseTitle = "دوره جدید";
    private string _blockTitle = string.Empty;
    private string _blockText = string.Empty;
    private string _blockFen = FenPosition.Initial;
    private bool _hasAutoAdvance;
    private double _autoAdvanceSeconds = 2;
    private string? _attachmentTargetId;
    private string _previewFen = FenPosition.Initial;
    private string _previewText = "یک Block را انتخاب کنید.";
    private string _previewStages = "۰ Stage";
    private BoardSkin _selectedSkin = BoardSkin.Chessmentor;
    private bool _showCoordinates = true;
    private string _status = "Course Builder آماده است.";
    private bool _isBusy;
    private bool _hasUnsavedChanges;
    private bool _syncingInspector;
    private bool _disposed;

    public CourseBuilderWindowViewModel(
        AppDatabase database,
        SettingsRepository settingsRepository,
        ViewerDocumentLoader loader)
    {
        _repository = new CourseBuilderRepository(database);
        _settingsRepository = settingsRepository;
        _loader = loader;
        _settingsRepository.Updated += OnSettingsUpdated;
        NewDocumentCommand = new AsyncRelayCommand(NewDocumentAsync, () => !IsBusy);
        AddTextCommand = new RelayCommand(() => AddBlock(CourseBlockKind.Text), CanEdit);
        AddPositionCommand = new RelayCommand(() => AddBlock(CourseBlockKind.Position), CanEdit);
        AddInteractiveMoveCommand = new RelayCommand(() => AddBlock(CourseBlockKind.InteractiveMove), CanEdit);
        AddStageCommand = new RelayCommand(() => AddBlock(CourseBlockKind.Stage), CanEdit);
        DeleteCommand = new RelayCommand(DeleteSelected, HasSelection);
        DuplicateCommand = new RelayCommand(DuplicateSelected, HasSelection);
        MoveUpCommand = new RelayCommand(() => MoveSelected(-1), HasSelection);
        MoveDownCommand = new RelayCommand(() => MoveSelected(1), HasSelection);
        UndoCommand = new RelayCommand(Undo, () => _editor?.CanUndo == true && !IsBusy);
        RedoCommand = new RelayCommand(Redo, () => _editor?.CanRedo == true && !IsBusy);
        SaveCommand = new AsyncRelayCommand(ExplicitSaveAsync, CanEdit);
    }

    public ObservableCollection<CourseBuilderDocumentSummary> Documents { get; } = new();
    public ObservableCollection<CourseSourceItem> Sources { get; } = new();
    public ObservableCollection<CourseBlockListItem> Blocks { get; } = new();
    public ObservableCollection<CourseAttachmentTarget> AttachmentTargets { get; } = new();

    public AsyncRelayCommand NewDocumentCommand { get; }
    public RelayCommand AddTextCommand { get; }
    public RelayCommand AddPositionCommand { get; }
    public RelayCommand AddInteractiveMoveCommand { get; }
    public RelayCommand AddStageCommand { get; }
    public RelayCommand DeleteCommand { get; }
    public RelayCommand DuplicateCommand { get; }
    public RelayCommand MoveUpCommand { get; }
    public RelayCommand MoveDownCommand { get; }
    public RelayCommand UndoCommand { get; }
    public RelayCommand RedoCommand { get; }
    public AsyncRelayCommand SaveCommand { get; }

    public CourseBuilderDocumentSummary? SelectedDocument
    {
        get => _selectedDocument;
        set => SetProperty(ref _selectedDocument, value);
    }

    public CourseBlockListItem? SelectedBlock
    {
        get => _selectedBlock;
        set
        {
            if (SetProperty(ref _selectedBlock, value))
            {
                LoadInspector(value?.Block);
                UpdateCommandStates();
            }
        }
    }

    public string CourseTitle
    {
        get => _courseTitle;
        set
        {
            if (!SetProperty(ref _courseTitle, value) || _syncingInspector || _editor is null)
            {
                return;
            }

            if (_editor.Rename(value))
            {
                ScheduleAutosave();
            }
        }
    }

    public string BlockTitle
    {
        get => _blockTitle;
        set
        {
            if (SetProperty(ref _blockTitle, value) && !_syncingInspector)
            {
                UpdateSelected(block => block with { Title = value });
            }
        }
    }

    public string BlockText
    {
        get => _blockText;
        set
        {
            if (SetProperty(ref _blockText, value) && !_syncingInspector)
            {
                UpdateSelected(block => block with { Text = value });
            }
        }
    }

    public string BlockFen
    {
        get => _blockFen;
        set
        {
            if (SetProperty(ref _blockFen, value) && !_syncingInspector)
            {
                UpdateSelected(block => block with { Fen = string.IsNullOrWhiteSpace(value) ? null : value.Trim() });
            }
        }
    }

    public bool HasAutoAdvance
    {
        get => _hasAutoAdvance;
        set
        {
            if (!SetProperty(ref _hasAutoAdvance, value) || _syncingInspector || _editor is null || SelectedBlock is null)
            {
                return;
            }

            if (_editor.SetAutoAdvance(SelectedBlock.Id, value ? AutoAdvanceSeconds : null))
            {
                AfterDocumentChanged(SelectedBlock.Id);
            }
        }
    }

    public double AutoAdvanceSeconds
    {
        get => _autoAdvanceSeconds;
        set
        {
            var normalized = Math.Clamp(value, 0.2, 120);
            if (!SetProperty(ref _autoAdvanceSeconds, normalized) || _syncingInspector || !HasAutoAdvance ||
                _editor is null || SelectedBlock is null)
            {
                return;
            }

            if (_editor.SetAutoAdvance(SelectedBlock.Id, normalized))
            {
                AfterDocumentChanged(SelectedBlock.Id);
            }
        }
    }

    public string? AttachmentTargetId
    {
        get => _attachmentTargetId;
        set
        {
            if (!SetProperty(ref _attachmentTargetId, value) || _syncingInspector ||
                _editor is null || SelectedBlock?.Block.Kind != CourseBlockKind.Text)
            {
                return;
            }

            var changed = string.IsNullOrWhiteSpace(value)
                ? _editor.DetachText(SelectedBlock.Id)
                : _editor.AttachText(SelectedBlock.Id, value);
            if (changed)
            {
                AfterDocumentChanged(SelectedBlock.Id);
            }
        }
    }

    public string PreviewFen { get => _previewFen; private set => SetProperty(ref _previewFen, value); }
    public string PreviewText { get => _previewText; private set => SetProperty(ref _previewText, value); }
    public string PreviewStages { get => _previewStages; private set => SetProperty(ref _previewStages, value); }
    public BoardSkin SelectedSkin { get => _selectedSkin; private set => SetProperty(ref _selectedSkin, value); }
    public bool ShowCoordinates { get => _showCoordinates; private set => SetProperty(ref _showCoordinates, value); }
    public string Status { get => _status; private set => SetProperty(ref _status, value); }

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

    public async Task InitializeAsync()
    {
        ApplySettings(await _settingsRepository.LoadAsync(_lifetime.Token).ConfigureAwait(true));
        await ReloadDocumentsAsync(_lifetime.Token).ConfigureAwait(true);
        if (Documents.FirstOrDefault() is { } first)
        {
            SelectedDocument = first;
            await LoadDocumentAsync(first, _lifetime.Token).ConfigureAwait(true);
        }
        else
        {
            await NewDocumentAsync(_lifetime.Token).ConfigureAwait(true);
        }
    }

    public async Task LoadDocumentAsync(CourseBuilderDocumentSummary summary, CancellationToken cancellationToken = default)
    {
        if (IsBusy || summary.Id == _editor?.Current.Id)
        {
            return;
        }

        if (_hasUnsavedChanges && !await FlushAsync().ConfigureAwait(true))
        {
            Status = "تعویض سند متوقف شد چون ذخیره سند فعلی ناموفق بود.";
            return;
        }

        IsBusy = true;
        CancelAutosave();
        try
        {
            var document = await _repository.GetAsync(summary.Id, EffectiveToken(cancellationToken)).ConfigureAwait(true);
            if (document is null)
            {
                Status = "سند انتخاب‌شده در دیتابیس پیدا نشد.";
                return;
            }

            SetDocument(document);
            SelectedDocument = summary;
            Status = $"«{document.Title}» باز شد.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task LoadPgnFilesAsync(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0 || IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var batch = await _loader.LoadAsync(paths, _lifetime.Token).ConfigureAwait(true);
            Sources.Clear();
            foreach (var source in batch.Sources)
            {
                foreach (var item in CourseSourceCatalog.FromDocument(source.Document, source.FilePath))
                {
                    Sources.Add(item);
                }
            }

            Status = $"{batch.GameCount:N0} بازی و {Sources.Count:N0} Source در پس‌زمینه آماده شد.";
        }
        catch (Exception exception)
        {
            Status = $"باز کردن PGN ناموفق بود: {exception.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    public void AddSource(CourseSourceItem source)
    {
        if (_editor is null || IsBusy)
        {
            return;
        }

        var kind = source.Kind switch
        {
            CourseSourceKind.Comment => CourseBlockKind.Text,
            CourseSourceKind.Position => CourseBlockKind.Position,
            CourseSourceKind.Variation => CourseBlockKind.Variation,
            CourseSourceKind.Move => CourseBlockKind.InteractiveMove,
            _ => CourseBlockKind.Text,
        };
        var block = _editor.Add(kind, source.Reference, source.Text, source.Fen);
        RefreshBlocks(block.Id);
        Status = $"Source «{source.Label}» به Canvas اضافه شد.";
        ScheduleAutosave();
    }

    public async Task<bool> FlushAsync()
    {
        if (!_hasUnsavedChanges || _editor is null)
        {
            return true;
        }

        CancelAutosave();
        await SaveAsync("close-autosave", _lifetime.Token).ConfigureAwait(true);
        return !_hasUnsavedChanges;
    }

    private async Task NewDocumentAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_hasUnsavedChanges && !await FlushAsync().ConfigureAwait(true))
        {
            Status = "ساخت سند جدید متوقف شد چون ذخیره سند فعلی ناموفق بود.";
            return;
        }

        CancelAutosave();
        SetDocument(CourseBuilderDocument.Create());
        SelectedDocument = null;
        Sources.Clear();
        Status = "سند جدید ساخته شد؛ PGN را باز کنید یا Block اضافه کنید.";
    }

    private void SetDocument(CourseBuilderDocument document)
    {
        _editor = new CourseDocumentEditor(document);
        _hasUnsavedChanges = false;
        _syncingInspector = true;
        CourseTitle = document.Title;
        _syncingInspector = false;
        RefreshBlocks();
        UpdateCommandStates();
    }

    private void AddBlock(CourseBlockKind kind)
    {
        if (_editor is null)
        {
            return;
        }

        var fen = kind is CourseBlockKind.Position or CourseBlockKind.InteractiveMove ? PreviewFen : null;
        var block = _editor.Add(kind, fen: fen);
        RefreshBlocks(block.Id);
        ScheduleAutosave();
    }

    private void DeleteSelected()
    {
        if (_editor is null || SelectedBlock is null || !_editor.Delete(SelectedBlock.Id))
        {
            return;
        }

        RefreshBlocks();
        ScheduleAutosave();
    }

    private void DuplicateSelected()
    {
        if (_editor is null || SelectedBlock is null)
        {
            return;
        }

        var copy = _editor.Duplicate(SelectedBlock.Id);
        RefreshBlocks(copy.Id);
        ScheduleAutosave();
    }

    private void MoveSelected(int delta)
    {
        if (_editor is null || SelectedBlock is null)
        {
            return;
        }

        var index = _editor.Current.Blocks.ToList().FindIndex(block => block.Id == SelectedBlock.Id);
        if (_editor.Move(SelectedBlock.Id, index + delta))
        {
            RefreshBlocks(SelectedBlock.Id);
            ScheduleAutosave();
        }
    }

    private void Undo()
    {
        if (_editor?.Undo() == true)
        {
            RefreshBlocks();
            ScheduleAutosave();
        }
    }

    private void Redo()
    {
        if (_editor?.Redo() == true)
        {
            RefreshBlocks();
            ScheduleAutosave();
        }
    }

    private void UpdateSelected(Func<CourseBlock, CourseBlock> update)
    {
        if (_editor is null || SelectedBlock is null)
        {
            return;
        }

        var id = SelectedBlock.Id;
        if (_editor.Replace(update(SelectedBlock.Block)))
        {
            AfterDocumentChanged(id);
        }
    }

    private void AfterDocumentChanged(string selectedId)
    {
        RefreshBlock(selectedId);
        ScheduleAutosave();
    }

    private void RefreshBlock(string blockId)
    {
        if (_editor is null)
        {
            return;
        }

        var model = _editor.Current.Blocks.FirstOrDefault(block => block.Id == blockId);
        if (model is null)
        {
            RefreshBlocks();
            return;
        }

        for (var index = 0; index < Blocks.Count; index++)
        {
            if (Blocks[index].Id != blockId)
            {
                continue;
            }

            var replacement = new CourseBlockListItem(model);
            Blocks[index] = replacement;
            SelectedBlock = replacement;
            break;
        }

        if (model.Kind != CourseBlockKind.Text)
        {
            for (var index = 0; index < AttachmentTargets.Count; index++)
            {
                if (AttachmentTargets[index].Id == blockId)
                {
                    AttachmentTargets[index] = new CourseAttachmentTarget(model.Id, model.Title, model.Kind);
                    break;
                }
            }
        }

        UpdateStageSummary();
        UpdateCommandStates();
    }

    private void RefreshBlocks(string? selectedId = null)
    {
        selectedId ??= SelectedBlock?.Id;
        Blocks.Clear();
        AttachmentTargets.Clear();
        if (_editor is null)
        {
            SelectedBlock = null;
            return;
        }

        foreach (var block in _editor.Current.Blocks)
        {
            Blocks.Add(new CourseBlockListItem(block));
            if (block.Kind != CourseBlockKind.Text)
            {
                AttachmentTargets.Add(new CourseAttachmentTarget(block.Id, block.Title, block.Kind));
            }
        }

        SelectedBlock = Blocks.FirstOrDefault(block => block.Id == selectedId) ?? Blocks.LastOrDefault();
        _syncingInspector = true;
        CourseTitle = _editor.Current.Title;
        _syncingInspector = false;
        UpdateStageSummary();
        UpdateCommandStates();
    }

    private void UpdateStageSummary()
    {
        if (_editor is null)
        {
            PreviewStages = "۰ Stage";
            return;
        }

        var stages = CourseStageCompiler.Compile(_editor.Current);
        PreviewStages = $"{stages.Count:N0} Stage از {_editor.Current.Blocks.Count:N0} Block";
    }

    private void LoadInspector(CourseBlock? block)
    {
        _syncingInspector = true;
        BlockTitle = block?.Title ?? string.Empty;
        BlockText = block?.Text ?? string.Empty;
        BlockFen = block?.Fen ?? FenPosition.Initial;
        HasAutoAdvance = block?.AutoAdvanceSeconds is not null;
        AutoAdvanceSeconds = block?.AutoAdvanceSeconds ?? 2;
        AttachmentTargetId = block?.AttachedToBlockId;
        _syncingInspector = false;
        PreviewFen = block?.Fen ?? FenPosition.Initial;
        PreviewText = block is null
            ? "یک Block را انتخاب کنید."
            : string.IsNullOrWhiteSpace(block.Text) ? block.Title : block.Text;
    }

    private void ScheduleAutosave()
    {
        CancelAutosave();
        _hasUnsavedChanges = true;
        _autosave = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _ = AutosaveAfterDelayAsync(_autosave.Token);
        Status = "تغییر ثبت شد؛ autosave در انتظار پایان تایپ است…";
        UpdateCommandStates();
    }

    private async Task AutosaveAfterDelayAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(800, cancellationToken).ConfigureAwait(true);
            await SaveAsync("autosave", cancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task ExplicitSaveAsync(CancellationToken cancellationToken)
    {
        CancelAutosave();
        await SaveAsync("explicit-save", cancellationToken).ConfigureAwait(true);
    }

    private async Task SaveAsync(string reason, CancellationToken cancellationToken)
    {
        if (_editor is null)
        {
            return;
        }

        var document = _editor.Current;
        IsBusy = true;
        try
        {
            var revision = await _repository.SaveAsync(document, reason, cancellationToken: EffectiveToken(cancellationToken)).ConfigureAwait(true);
            await ReloadDocumentsAsync(EffectiveToken(cancellationToken)).ConfigureAwait(true);
            SelectedDocument = Documents.FirstOrDefault(item => item.Id == document.Id);
            _hasUnsavedChanges = false;
            Status = $"ذخیره شد — Revision {revision:N0} ({reason}).";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Status = $"ذخیره ناموفق بود: {exception.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ReloadDocumentsAsync(CancellationToken cancellationToken)
    {
        var items = await _repository.ListAsync(cancellationToken).ConfigureAwait(true);
        Documents.Clear();
        foreach (var item in items)
        {
            Documents.Add(item);
        }
    }

    private void CancelAutosave()
    {
        _autosave?.Cancel();
        _autosave?.Dispose();
        _autosave = null;
    }

    private bool CanEdit() => _editor is not null && !IsBusy;
    private bool HasSelection() => SelectedBlock is not null && !IsBusy;

    private void UpdateCommandStates()
    {
        NewDocumentCommand.RaiseCanExecuteChanged();
        AddTextCommand.RaiseCanExecuteChanged();
        AddPositionCommand.RaiseCanExecuteChanged();
        AddInteractiveMoveCommand.RaiseCanExecuteChanged();
        AddStageCommand.RaiseCanExecuteChanged();
        DeleteCommand.RaiseCanExecuteChanged();
        DuplicateCommand.RaiseCanExecuteChanged();
        MoveUpCommand.RaiseCanExecuteChanged();
        MoveDownCommand.RaiseCanExecuteChanged();
        UndoCommand.RaiseCanExecuteChanged();
        RedoCommand.RaiseCanExecuteChanged();
        SaveCommand.RaiseCanExecuteChanged();
    }

    private void OnSettingsUpdated(AppSettings settings) => ApplySettings(settings);

    private void ApplySettings(AppSettings settings)
    {
        SelectedSkin = settings.BoardSkin;
        ShowCoordinates = settings.ShowCoordinates;
    }

    private CancellationToken EffectiveToken(CancellationToken token) =>
        token.CanBeCanceled ? token : _lifetime.Token;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _settingsRepository.Updated -= OnSettingsUpdated;
        CancelAutosave();
        NewDocumentCommand.Dispose();
        SaveCommand.Dispose();
        _lifetime.Cancel();
        _lifetime.Dispose();
    }
}

public sealed record CourseBlockListItem(CourseBlock Block)
{
    public string Id => Block.Id;
    public string Title => Block.Title;
    public string Kind => Block.Kind.ToString();
    public string Fen => Block.Fen ?? FenPosition.Initial;
    public bool IsBoardBlock => Block.Kind is CourseBlockKind.Position or CourseBlockKind.InteractiveMove or
        CourseBlockKind.MoveSequence or CourseBlockKind.Variation;
    public string Badge => string.Join("  ", new[]
    {
        Block.AutoAdvanceSeconds is { } seconds ? $"▶ {seconds:0.#}s" : string.Empty,
        Block.AttachedToBlockId is not null ? "LEGO" : string.Empty,
        Block.Source is not null ? "Source" : string.Empty,
    }.Where(static value => value.Length > 0));
}

public sealed record CourseAttachmentTarget(string Id, string Title, CourseBlockKind Kind)
{
    public string Label => $"{Title} ({Kind})";
}
