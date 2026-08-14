using System.Collections.ObjectModel;
using ChessMentor.Chess;
using ChessMentor.Core.Mvvm;
using ChessMentor.Pgn;

namespace ChessMentor.Viewer;

/// <summary>
/// Mutable Viewer 1 workspace. Navigation updates only the old and new active rows;
/// the flattened move collection is built once per game and remains virtualizable.
/// </summary>
public sealed class ViewerSession : ObservableObject
{
    private readonly Dictionary<string, PgnMoveNode> _nodeIndex = new(StringComparer.Ordinal);
    private ViewerGameItem? _activeGame;
    private PgnMoveNode? _currentNode;
    private ViewerMoveItem? _activeMoveItem;
    private bool _isBranchChooserOpen;

    public ObservableCollection<ViewerGameItem> Games { get; } = new();
    public ObservableCollection<ViewerBranchItem> Branches { get; } = new();
    public bool IsBulkUpdating { get; private set; }
    public event EventHandler? WorkspaceChanged;

    public ViewerGameItem? ActiveGame
    {
        get => _activeGame;
        private set => SetProperty(ref _activeGame, value);
    }

    public PgnMoveNode? CurrentNode
    {
        get => _currentNode;
        private set => SetProperty(ref _currentNode, value);
    }

    public ViewerMoveItem? ActiveMoveItem
    {
        get => _activeMoveItem;
        private set => SetProperty(ref _activeMoveItem, value);
    }

    public bool IsBranchChooserOpen
    {
        get => _isBranchChooserOpen;
        private set => SetProperty(ref _isBranchChooserOpen, value);
    }

    public string CurrentFen
    {
        get
        {
            for (var node = CurrentNode; node is not null; node = node.Parent)
            {
                if (!string.IsNullOrWhiteSpace(node.Fen))
                {
                    return node.Fen;
                }
            }

            return ActiveGame?.Game.Root.Fen ?? FenPosition.Empty;
        }
    }
    public bool HasGames => Games.Count > 0;
    public bool CanPreviousMove => CurrentNode?.Parent is not null;
    public bool CanNextMove => CurrentNode?.Children.Count > 0;
    public bool CanPreviousGame => ActiveGame is { Index: > 0 };
    public bool CanNextGame => ActiveGame is not null && ActiveGame.Index < Games.Count - 1;

    public void Replace(IReadOnlyList<LoadedPgnSource> sources)
    {
        Clear();
        AppendCore(sources);
        SelectGame(Games.FirstOrDefault());
    }

    public void Append(IReadOnlyList<LoadedPgnSource> sources)
    {
        var previouslyActive = ActiveGame;
        AppendCore(sources);
        if (previouslyActive is null)
        {
            SelectGame(Games.FirstOrDefault());
        }
        else
        {
            RaiseNavigationState();
        }
    }

    public void Clear()
    {
        foreach (var game in Games)
        {
            game.IsActive = false;
        }

        Games.Clear();
        _nodeIndex.Clear();
        SetActiveMoveItem(null);
        ActiveGame = null;
        CurrentNode = null;
        CloseBranchChooser();
        RaiseNavigationState();
    }

    public void SelectGame(ViewerGameItem? game)
    {
        if (game is null || !Games.Contains(game))
        {
            if (Games.Count == 0)
            {
                Clear();
            }

            return;
        }

        if (ActiveGame is not null)
        {
            ActiveGame.IsActive = false;
        }

        ActiveGame = game;
        game.IsActive = true;
        _nodeIndex.Clear();
        _nodeIndex[game.Game.Root.StableId] = game.Game.Root;
        foreach (var node in game.Game.Root.Descendants())
        {
            _nodeIndex[node.StableId] = node;
        }

        SetCurrentNode(game.Game.Root);
        CloseBranchChooser();
        RaiseNavigationState();
    }

    public bool SelectGameByOffset(int offset)
    {
        if (ActiveGame is null)
        {
            return false;
        }

        var index = ActiveGame.Index + offset;
        if (index < 0 || index >= Games.Count)
        {
            return false;
        }

        SelectGame(Games[index]);
        return true;
    }

    public bool SelectNode(string nodeId)
    {
        if (!_nodeIndex.TryGetValue(nodeId, out var node))
        {
            return false;
        }

        SetCurrentNode(node);
        CloseBranchChooser();
        return true;
    }

    public bool PreviousMove()
    {
        if (CurrentNode?.Parent is not { } parent)
        {
            return false;
        }

        SetCurrentNode(parent);
        CloseBranchChooser();
        return true;
    }

    public ViewerNavigationResult NextMove()
    {
        var children = CurrentNode?.Children;
        if (children is null || children.Count == 0)
        {
            return ViewerNavigationResult.None;
        }

        if (children.Count > 1)
        {
            OpenBranchChooser();
            return ViewerNavigationResult.BranchSelectionRequired;
        }

        SetCurrentNode(children[0]);
        return ViewerNavigationResult.Moved;
    }

    public bool SelectBranch(int index)
    {
        if (CurrentNode is null || index < 0 || index >= CurrentNode.Children.Count)
        {
            return false;
        }

        SetCurrentNode(CurrentNode.Children[index]);
        CloseBranchChooser();
        return true;
    }

    public bool SelectMainlineSibling()
    {
        var parent = CurrentNode?.Parent;
        if (parent?.Children.FirstOrDefault() is not { } mainline)
        {
            return false;
        }

        SetCurrentNode(mainline);
        CloseBranchChooser();
        return true;
    }

    public int RemoveMarked()
    {
        var removed = Games.Where(static game => game.IsMarked).ToArray();
        if (removed.Length == 0)
        {
            return 0;
        }

        var activeIndex = ActiveGame?.Index ?? 0;
        IsBulkUpdating = true;
        try
        {
            foreach (var game in removed)
            {
                Games.Remove(game);
            }
        }
        finally
        {
            IsBulkUpdating = false;
        }

        Reindex();
        if (Games.Count == 0)
        {
            Clear();
        }
        else if (ActiveGame is null || !Games.Contains(ActiveGame))
        {
            SelectGame(Games[Math.Min(activeIndex, Games.Count - 1)]);
        }
        else
        {
            RaiseNavigationState();
        }

        WorkspaceChanged?.Invoke(this, EventArgs.Empty);

        return removed.Length;
    }

    public bool Remove(ViewerGameItem game)
    {
        if (!Games.Contains(game))
        {
            return false;
        }

        var removedIndex = game.Index;
        var wasActive = ReferenceEquals(game, ActiveGame);
        Games.Remove(game);
        Reindex();
        if (Games.Count == 0)
        {
            Clear();
        }
        else if (wasActive)
        {
            SelectGame(Games[Math.Min(removedIndex, Games.Count - 1)]);
        }
        else
        {
            RaiseNavigationState();
        }

        return true;
    }

    public bool RefreshActiveGameTree(string? preferredNodeId = null)
    {
        var current = ActiveGame;
        if (current is null || current.Index < 0 || current.Index >= Games.Count)
        {
            return false;
        }

        var nodeId = preferredNodeId ?? CurrentNode?.StableId ?? current.Game.Root.StableId;
        var replacement = new ViewerGameItem(current.Game, current.SourceFileName, current.Index)
        {
            IsMarked = current.IsMarked,
        };
        IsBulkUpdating = true;
        try
        {
            Games[current.Index] = replacement;
        }
        finally
        {
            IsBulkUpdating = false;
        }

        SelectGame(replacement);
        _ = SelectNode(nodeId);
        WorkspaceChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public void CloseBranchChooser()
    {
        IsBranchChooserOpen = false;
        Branches.Clear();
    }

    private void OpenBranchChooser()
    {
        Branches.Clear();
        if (CurrentNode is null)
        {
            IsBranchChooserOpen = false;
            return;
        }

        for (var index = 0; index < CurrentNode.Children.Count; index++)
        {
            Branches.Add(new ViewerBranchItem(CurrentNode.Children[index], index));
        }

        IsBranchChooserOpen = Branches.Count > 1;
    }

    private void AppendCore(IReadOnlyList<LoadedPgnSource> sources)
    {
        IsBulkUpdating = true;
        try
        {
            foreach (var source in sources)
            {
                foreach (var game in source.Document.Games)
                {
                    Games.Add(new ViewerGameItem(game, source.FileName, Games.Count));
                }
            }
        }
        finally
        {
            IsBulkUpdating = false;
        }

        Reindex();
        OnPropertyChanged(nameof(HasGames));
        WorkspaceChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Reindex()
    {
        for (var index = 0; index < Games.Count; index++)
        {
            Games[index].Reindex(index);
        }

        OnPropertyChanged(nameof(HasGames));
    }

    private void SetCurrentNode(PgnMoveNode node)
    {
        CurrentNode = node;
        var item = ActiveGame?.FindMove(node.StableId);
        SetActiveMoveItem(item);
        OnPropertyChanged(nameof(CurrentFen));
        RaiseNavigationState();
    }

    private void SetActiveMoveItem(ViewerMoveItem? item)
    {
        if (ActiveMoveItem is not null)
        {
            ActiveMoveItem.IsActive = false;
        }

        ActiveMoveItem = item;
        if (item is not null)
        {
            item.IsActive = true;
        }
    }

    private void RaiseNavigationState()
    {
        OnPropertyChanged(nameof(CanPreviousMove));
        OnPropertyChanged(nameof(CanNextMove));
        OnPropertyChanged(nameof(CanPreviousGame));
        OnPropertyChanged(nameof(CanNextGame));
        OnPropertyChanged(nameof(CurrentFen));
    }
}
