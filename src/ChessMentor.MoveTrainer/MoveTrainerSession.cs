namespace ChessMentor.MoveTrainer;

public sealed class MoveTrainerSession
{
    private readonly TrainerAnswerEvaluator _evaluator;
    private readonly TrainerCourseSettings _settings;
    private readonly List<TrainerItem> _queue;
    private readonly Dictionary<string, TrainerSessionItemState> _states = new(StringComparer.Ordinal);
    private readonly HashSet<string> _mistakes = new(StringComparer.Ordinal);
    private int _currentIndex;

    public MoveTrainerSession(
        IEnumerable<TrainerItem> items,
        TrainerCourseSettings settings,
        TrainerAnswerEvaluator? evaluator = null,
        string? sessionId = null,
        TrainerSessionSnapshot? restoredSnapshot = null)
    {
        ArgumentNullException.ThrowIfNull(items);
        _settings = settings.Normalize();
        _evaluator = evaluator ?? new TrainerAnswerEvaluator();
        var enabledItems = items
            .Where(static item => item.Enabled)
            .ToArray();
        var available = enabledItems.ToDictionary(static item => item.Id, StringComparer.Ordinal);
        _queue = restoredSnapshot is null
            ? enabledItems.Take(_settings.MaxSessionItems).ToList()
            : restoredSnapshot.Items
                .Select(state => available.GetValueOrDefault(state.ItemId))
                .Where(static item => item is not null)
                .Select(static item => item!)
                .Take(_settings.MaxSessionItems)
                .ToList();
        foreach (var item in _queue)
        {
            var restored = restoredSnapshot?.Items.FirstOrDefault(state =>
                string.Equals(state.ItemId, item.Id, StringComparison.Ordinal));
            _states[item.Id] = restored ?? new TrainerSessionItemState(item.Id, 0, null, false);
        }

        if (restoredSnapshot is not null)
        {
            foreach (var itemId in restoredSnapshot.MistakeItemIds.Where(available.ContainsKey))
            {
                _mistakes.Add(itemId);
            }

            _currentIndex = Math.Clamp(restoredSnapshot.CurrentIndex, 0, _queue.Count);
        }

        SessionId = string.IsNullOrWhiteSpace(sessionId) ? $"session_{Guid.NewGuid():N}" : sessionId;
    }

    public static MoveTrainerSession Restore(
        IEnumerable<TrainerItem> items,
        TrainerCourseSettings settings,
        TrainerSessionSnapshot snapshot,
        TrainerAnswerEvaluator? evaluator = null) =>
        new(items, settings, evaluator, snapshot.SessionId, snapshot);

    public string SessionId { get; }
    public TrainerItem? CurrentItem => _currentIndex < _queue.Count ? _queue[_currentIndex] : null;
    public int CurrentIndex => _currentIndex;
    public int Total => _queue.Count;
    public bool IsComplete => CurrentItem is null;

    public TrainerEvaluation Submit(
        TrainerAttemptRequest request,
        CancellationToken cancellationToken = default)
    {
        var item = CurrentItem ?? throw new InvalidOperationException("The training session is complete.");
        var result = _evaluator.Evaluate(item, request, _settings.AcceptTranspositions, cancellationToken);
        var previous = _states[item.Id];
        var completed = result.CompletesItem || !_settings.AllowRetry;
        _states[item.Id] = previous with
        {
            AttemptCount = previous.AttemptCount + 1,
            Outcome = result.Outcome,
            Completed = completed,
        };
        if (result.Outcome == TrainerOutcome.Wrong)
        {
            _mistakes.Add(item.Id);
        }

        if (completed)
        {
            _currentIndex++;
        }

        return result;
    }

    public bool RetryMistakes()
    {
        if (_mistakes.Count == 0)
        {
            return false;
        }

        var retry = _queue.Where(item => _mistakes.Contains(item.Id)).ToArray();
        _queue.Clear();
        _queue.AddRange(retry);
        _states.Clear();
        foreach (var item in retry)
        {
            _states[item.Id] = new TrainerSessionItemState(item.Id, 0, null, false);
        }

        _mistakes.Clear();
        _currentIndex = 0;
        return true;
    }

    public TrainerSessionSnapshot Snapshot() =>
        new(
            SessionId,
            _currentIndex,
            _queue.Select(item => _states[item.Id]).ToArray(),
            _mistakes.Order(StringComparer.Ordinal).ToArray(),
            IsComplete);
}
