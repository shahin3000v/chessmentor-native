using System.Text.Json;
using ChessMentor.Chess;
using ChessMentor.Pgn;
using ChessMentor.Translation;
using ChessMentor.Viewer;

namespace ChessMentor.Studio;

public sealed class StudioWorkspace
{
    private readonly Dictionary<StudioCommentKey, StudioTranslationLink> _translationLinks = [];
    private readonly List<string> _sourceNames = [];

    public ViewerSession Session { get; } = new();
    public bool IsDirty { get; private set; }
    public IReadOnlyList<string> SourceNames => _sourceNames;
    public event EventHandler? Changed;

    public void Replace(IReadOnlyList<LoadedPgnSource> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        _sourceNames.Clear();
        _sourceNames.AddRange(sources.Select(static source => source.FileName));
        _translationLinks.Clear();
        Session.Replace(sources);
        SetDirty(false);
    }

    public void Append(IReadOnlyList<LoadedPgnSource> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        _sourceNames.AddRange(sources.Select(static source => source.FileName));
        Session.Append(sources);
        SetDirty(true);
    }

    public bool RemoveGame(ViewerGameItem game)
    {
        if (!Session.Remove(game))
        {
            return false;
        }

        var keys = _translationLinks.Keys.Where(key =>
            string.Equals(key.GameId, game.Game.Id, StringComparison.Ordinal)).ToArray();
        foreach (var key in keys)
        {
            _translationLinks.Remove(key);
        }

        SetDirty(true);
        return true;
    }

    public int RemoveMarkedGames()
    {
        var removedIds = Session.Games.Where(static game => game.IsMarked)
            .Select(static game => game.Game.Id)
            .ToHashSet(StringComparer.Ordinal);
        var removed = Session.RemoveMarked();
        if (removed == 0)
        {
            return 0;
        }

        var keys = _translationLinks.Keys.Where(key => removedIds.Contains(key.GameId)).ToArray();
        foreach (var key in keys)
        {
            _translationLinks.Remove(key);
        }

        SetDirty(true);
        return removed;
    }

    public void EditCurrentComments(string? startingComment, string? comment)
    {
        var activeGame = Session.ActiveGame?.Game;
        var node = Session.CurrentNode;
        if (activeGame is null || node is null)
        {
            return;
        }

        PgnTreeEditor.SetStartingComment(node, startingComment);
        PgnTreeEditor.SetComment(node, comment);
        _translationLinks.Remove(new StudioCommentKey(activeGame.Id, node.StableId, "startingComment"));
        _translationLinks.Remove(new StudioCommentKey(activeGame.Id, node.StableId, "comment"));
        Session.RefreshActiveGameTree(node.StableId);
        SetDirty(true);
    }

    public PgnMoveInsertResult AddMove(LegalMove move, string resultingFen)
    {
        var game = Session.ActiveGame?.Game ?? throw new InvalidOperationException("No active Studio game.");
        var parent = Session.CurrentNode ?? throw new InvalidOperationException("No active Studio node.");
        var result = PgnTreeEditor.AddMove(game, parent, move, resultingFen);
        Session.RefreshActiveGameTree(result.Node.StableId);
        if (result.Created)
        {
            SetDirty(true);
        }

        return result;
    }

    public bool DeleteCurrentBranch()
    {
        var game = Session.ActiveGame?.Game;
        var node = Session.CurrentNode;
        var parent = node?.Parent;
        if (game is null || node is null || parent is null || !PgnTreeEditor.DeleteBranch(node))
        {
            return false;
        }

        foreach (var deleted in node.Descendants().Prepend(node))
        {
            _translationLinks.Remove(new StudioCommentKey(game.Id, deleted.StableId, "startingComment"));
            _translationLinks.Remove(new StudioCommentKey(game.Id, deleted.StableId, "comment"));
        }

        Session.RefreshActiveGameTree(parent.StableId);
        SetDirty(true);
        return true;
    }

    public bool PromoteCurrentBranch()
    {
        var node = Session.CurrentNode;
        if (node is null || !PgnTreeEditor.PromoteToMainline(node))
        {
            return false;
        }

        Session.RefreshActiveGameTree(node.StableId);
        SetDirty(true);
        return true;
    }

    public IReadOnlyList<TranslationWorkItem> CollectTranslationWork(string? courseId = null)
    {
        var work = new List<TranslationWorkItem>();
        foreach (var gameItem in Session.Games)
        {
            foreach (var node in gameItem.Game.Root.Descendants().Prepend(gameItem.Game.Root))
            {
                AddTranslationWork(
                    work,
                    courseId,
                    gameItem.Game.Id,
                    node,
                    "startingComment",
                    PgnTreeEditor.StartingCommentText(node));
                AddTranslationWork(
                    work,
                    courseId,
                    gameItem.Game.Id,
                    node,
                    "comment",
                    PgnTreeEditor.CommentText(node));
            }
        }

        return work;
    }

    public bool ApplyTranslation(TranslationApplied applied, bool refreshPresentation = true)
    {
        ArgumentNullException.ThrowIfNull(applied);
        var game = Session.Games.FirstOrDefault(item =>
            string.Equals(item.Game.Id, applied.Item.GameId, StringComparison.Ordinal));
        if (game is null || applied.Item.NodeId is null)
        {
            return false;
        }

        var node = game.Game.Root.Descendants().Prepend(game.Game.Root).FirstOrDefault(candidate =>
            string.Equals(candidate.StableId, applied.Item.NodeId, StringComparison.Ordinal));
        if (node is null)
        {
            return false;
        }

        if (string.Equals(applied.Item.Field, "startingComment", StringComparison.Ordinal))
        {
            PgnTreeEditor.SetStartingComment(node, applied.TranslatedText);
        }
        else if (string.Equals(applied.Item.Field, "comment", StringComparison.Ordinal))
        {
            PgnTreeEditor.SetComment(node, applied.TranslatedText);
        }
        else
        {
            return false;
        }

        var link = new StudioTranslationLink(
            game.Game.Id,
            node.StableId,
            applied.Item.Field,
            applied.SourceHash,
            applied.Item.SourceText);
        _translationLinks[new StudioCommentKey(game.Game.Id, node.StableId, applied.Item.Field)] = link;
        if (refreshPresentation && ReferenceEquals(Session.ActiveGame, game))
        {
            Session.RefreshActiveGameTree(node.StableId);
        }

        SetDirty(true);
        return true;
    }

    public int ApplyTranslationMemoryUpdate(
        string sourceHash,
        string sourceText,
        string translatedText)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceHash);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceText);
        ArgumentException.ThrowIfNullOrWhiteSpace(translatedText);
        var matching = _translationLinks.Values.Where(link =>
                string.Equals(link.SourceHash, sourceHash, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(link.SourceText, sourceText, StringComparison.Ordinal))
            .ToArray();
        var changed = 0;
        foreach (var link in matching)
        {
            var game = Session.Games.FirstOrDefault(item =>
                string.Equals(item.Game.Id, link.GameId, StringComparison.Ordinal));
            if (game is null)
            {
                continue;
            }

            var node = game.Game.Root.Descendants().Prepend(game.Game.Root).FirstOrDefault(candidate =>
                string.Equals(candidate.StableId, link.NodeId, StringComparison.Ordinal));
            if (node is null)
            {
                continue;
            }

            var current = string.Equals(link.Field, "startingComment", StringComparison.Ordinal)
                ? PgnTreeEditor.StartingCommentText(node)
                : string.Equals(link.Field, "comment", StringComparison.Ordinal)
                    ? PgnTreeEditor.CommentText(node)
                    : null;
            if (current is null || string.Equals(current, translatedText, StringComparison.Ordinal))
            {
                continue;
            }

            if (string.Equals(link.Field, "startingComment", StringComparison.Ordinal))
            {
                PgnTreeEditor.SetStartingComment(node, translatedText);
            }
            else
            {
                PgnTreeEditor.SetComment(node, translatedText);
            }

            changed++;
        }

        if (changed > 0)
        {
            Session.RefreshActiveGameTree(Session.CurrentNode?.StableId);
            SetDirty(true);
        }

        return changed;
    }

    public void RefreshActivePresentation(string? preferredNodeId = null) =>
        Session.RefreshActiveGameTree(preferredNodeId ?? Session.CurrentNode?.StableId);

    public string ExportPgn() =>
        PgnAstSerializer.SerializeGames(Session.Games.Select(static game => game.Game));

    public JsonElement BuildServerPayload() =>
        StudioServerPayload.Build(Session.Games, _translationLinks);

    public bool TryGetTranslationLink(
        string gameId,
        string nodeId,
        string field,
        out StudioTranslationLink? link) =>
        _translationLinks.TryGetValue(new StudioCommentKey(gameId, nodeId, field), out link);

    public void SetTranslationLink(StudioTranslationLink link)
    {
        ArgumentNullException.ThrowIfNull(link);
        _translationLinks[new StudioCommentKey(link.GameId, link.NodeId, link.Field)] = link;
    }

    public StudioDraftPackage CreateDraftPackage(
        string draftId,
        string? sourceId,
        string title,
        long? serverDraftId,
        string categorySlug,
        string publishSlug,
        int creditPriceMinor,
        string? featuredImagePath = null,
        string? featuredImageName = null,
        long? serverCourseId = null) =>
        new(
            StudioDraftPackage.CurrentSchemaVersion,
            draftId,
            sourceId,
            title,
            ExportPgn(),
            _sourceNames.ToArray(),
            Session.ActiveGame?.Game.Id,
            Session.CurrentNode?.StableId,
            _translationLinks.Values.ToArray(),
            null,
            serverDraftId,
            categorySlug,
            publishSlug,
            creditPriceMinor,
            DateTimeOffset.UtcNow,
            featuredImagePath,
            featuredImageName,
            serverCourseId,
            Session.Games.Select(static game => PgnTreeEditor.CaptureFlatIdentity(game.Game)).ToArray());

    public async Task RestoreAsync(
        StudioDraftPackage package,
        ViewerDocumentLoader loader,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(loader);
        if (package.SchemaVersion > StudioDraftPackage.CurrentSchemaVersion)
        {
            throw new InvalidDataException("This Studio draft was created by a newer desktop version.");
        }

        var loaded = await loader.LoadTextAsync(
            package.PgnText,
            package.SourceNames.FirstOrDefault() ?? $"{package.Title}.pgn",
            cancellationToken).ConfigureAwait(true);
        if (loaded.Sources.Count == 0)
        {
            throw new InvalidDataException(loaded.Diagnostics.FirstOrDefault() ?? "Draft PGN is empty.");
        }

        if (package.FlatGameIdentities is { Count: > 0 })
        {
            var games = loaded.Sources.SelectMany(static source => source.Document.Games).ToArray();
            if (games.Length != package.FlatGameIdentities.Count)
            {
                throw new InvalidDataException("Draft flat identity count does not match its PGN game count.");
            }

            for (var index = 0; index < games.Length; index++)
            {
                PgnTreeEditor.ApplyFlatIdentity(games[index], package.FlatGameIdentities[index]);
            }
        }
        else if (package.GameIdentities is { Count: > 0 })
        {
            var games = loaded.Sources.SelectMany(static source => source.Document.Games).ToArray();
            if (games.Length != package.GameIdentities.Count)
            {
                throw new InvalidDataException("Draft identity count does not match its PGN game count.");
            }

            for (var index = 0; index < games.Length; index++)
            {
                PgnTreeEditor.ApplyIdentity(games[index], package.GameIdentities[index]);
            }
        }

        _sourceNames.Clear();
        _sourceNames.AddRange(package.SourceNames);
        Session.Replace(loaded.Sources);
        _translationLinks.Clear();
        foreach (var link in package.TranslationLinks)
        {
            _translationLinks[new StudioCommentKey(link.GameId, link.NodeId, link.Field)] = link;
        }

        var selectedGame = Session.Games.FirstOrDefault(game =>
            string.Equals(game.Game.Id, package.ActiveGameId, StringComparison.Ordinal));
        if (selectedGame is not null)
        {
            Session.SelectGame(selectedGame);
            if (package.ActiveNodeId is not null)
            {
                _ = Session.SelectNode(package.ActiveNodeId);
            }
        }

        SetDirty(false);
    }

    public void MarkSaved() => SetDirty(false);

    private void AddTranslationWork(
        ICollection<TranslationWorkItem> work,
        string? courseId,
        string gameId,
        PgnMoveNode node,
        string field,
        string sourceText)
    {
        if (!PhraseIdentity.ShouldTranslate(sourceText))
        {
            return;
        }

        var identity = PhraseIdentity.Create(sourceText);
        work.Add(new TranslationWorkItem(
            identity,
            sourceText,
            "en",
            "fa",
            courseId,
            gameId,
            node.StableId,
            field));
    }

    private void SetDirty(bool value)
    {
        IsDirty = value;
        Changed?.Invoke(this, EventArgs.Empty);
    }

}

public readonly record struct StudioCommentKey(string GameId, string NodeId, string Field);
