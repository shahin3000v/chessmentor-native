using System.Text.Json;
using System.Text;
using ChessMentor.Pgn;
using ChessMentor.Studio;
using ChessMentor.Translation;
using ChessMentor.Viewer;

namespace ChessMentor.Tests;

public sealed class StudioWorkspaceTests
{
    [Fact]
    public async Task DraftRoundTripPreservesStableIdsTranslationsAndNestedVariations()
    {
        var token = TestContext.Current.CancellationToken;
        var loader = new ViewerDocumentLoader();
        var loaded = await loader.LoadTextAsync(
            "[Event \"Studio\"]\n[Result \"*\"]\n\n1. e4 {White keeps the initiative.} e5 (1... c5 !? {Sicilian pressure.}) 2. Nf3 *",
            "studio.pgn",
            token);
        var workspace = new StudioWorkspace();
        workspace.Replace(loaded.Sources);
        var work = workspace.CollectTranslationWork();
        var source = work.Single(item => item.SourceText == "White keeps the initiative.");
        Assert.True(workspace.ApplyTranslation(new TranslationApplied(
            source,
            PhraseIdentity.Create(source.SourceText),
            "سفید ابتکار عمل را حفظ می‌کند.",
            "server")));
        var originalGameId = Assert.Single(workspace.Session.Games).Game.Id;
        var originalNodeIds = workspace.Session.Games[0].Game.Root.Descendants()
            .Select(static node => node.StableId)
            .ToArray();
        var package = workspace.CreateDraftPackage(
            "draft-1",
            "source-1",
            "عنوان",
            17,
            "training",
            "slug",
            2500,
            @"C:\Users\author\cover.png",
            "cover.png",
            serverCourseId: 41);
        var serialized = JsonSerializer.Serialize(package);
        var rehydrated = JsonSerializer.Deserialize<StudioDraftPackage>(serialized);
        Assert.NotNull(rehydrated);
        Assert.Equal(@"C:\Users\author\cover.png", rehydrated.FeaturedImagePath);
        Assert.Equal("cover.png", rehydrated.FeaturedImageName);
        Assert.Equal(41L, rehydrated.ServerCourseId);

        var restored = new StudioWorkspace();
        await restored.RestoreAsync(rehydrated, loader, token);
        var game = Assert.Single(restored.Session.Games).Game;
        Assert.Equal(originalGameId, game.Id);
        Assert.Equal(originalNodeIds, game.Root.Descendants().Select(static node => node.StableId));
        var e4 = Assert.Single(game.Root.Children);
        Assert.Equal("سفید ابتکار عمل را حفظ می‌کند.", PgnTreeEditor.CommentText(e4));
        Assert.Equal(2, e4.Children.Count);
        Assert.Contains(5, e4.Children.Single(node => node.Uci == "c7c5").Nags);

        var payload = restored.BuildServerPayload();
        Assert.Equal(originalGameId, payload.GetProperty("games")[0].GetProperty("id").GetString());
        var payloadE4 = payload.GetProperty("games")[0].GetProperty("root").GetProperty("children")[0];
        Assert.Equal(source.SourceText, payloadE4.GetProperty("commentSourceText").GetString());
        Assert.Equal(PhraseIdentity.Create(source.SourceText), payloadE4.GetProperty("commentSourceHash").GetString());
        var payloadC5 = payloadE4.GetProperty("children")[1];
        Assert.Equal("!?", payloadC5.GetProperty("annotations")[0].GetString());
    }

    [Fact]
    public async Task DeepMainlineDraftUsesFlatIdentitiesAndDoesNotHitJsonDepthLimit()
    {
        var token = TestContext.Current.CancellationToken;
        var pgn = new StringBuilder("[Event \"Deep audio draft\"]\n[Result \"*\"]\n\n");
        for (var cycle = 0; cycle < 100; cycle++)
        {
            var number = (cycle * 2) + 1;
            pgn.Append(number).Append(". Nf3 Nf6 ")
                .Append(number + 1).Append(". Ng1 Ng8 ");
        }

        pgn.Append('*');
        var loader = new ViewerDocumentLoader();
        var loaded = await loader.LoadTextAsync(pgn.ToString(), "deep.pgn", token);
        Assert.Empty(loaded.Diagnostics);
        var workspace = new StudioWorkspace();
        workspace.Replace(loaded.Sources);
        var original = Assert.Single(workspace.Session.Games).Game;
        var originalLastId = original.Root.Descendants().Last().StableId;

        var package = workspace.CreateDraftPackage(
            "draft-deep",
            "deep.pgn",
            "Deep",
            null,
            "training",
            string.Empty,
            0);

        Assert.Null(package.GameIdentities);
        var flat = Assert.Single(package.FlatGameIdentities!);
        Assert.Equal(401, flat.Nodes.Count);
        var json = JsonSerializer.Serialize(package);
        Assert.DoesNotContain("\"children\"", json, StringComparison.OrdinalIgnoreCase);
        var rehydrated = JsonSerializer.Deserialize<StudioDraftPackage>(json);
        Assert.NotNull(rehydrated);

        var restored = new StudioWorkspace();
        await restored.RestoreAsync(rehydrated, loader, token);
        var restoredGame = Assert.Single(restored.Session.Games).Game;
        Assert.Equal(original.Id, restoredGame.Id);
        Assert.Equal(originalLastId, restoredGame.Root.Descendants().Last().StableId);
    }

    [Fact]
    public async Task ServerPayloadReadRestoresExactExternalGameAndNodeIds()
    {
        var token = TestContext.Current.CancellationToken;
        using var json = JsonDocument.Parse("""
            {
              "games": [{
                "headers": {"Event":"Server", "Result":"*"},
                "root": {
                  "id":"g7", "san":"", "uci":"", "ply":0,
                  "comment":"", "startingComment":"", "nags":[],
                  "children":[{
                    "id":"g7.3", "san":"e4", "uci":"e2e4", "ply":1,
                    "comment":"ترجمه", "commentSourceHash":"fd248d318fff9be72fa42f3b9dbbeb770b92cb0e10d6c05723448a6ba479af0f",
                    "commentSourceText":"White keeps the initiative.",
                    "startingComment":"", "annotations":["!?"], "nags":[5], "children":[]
                  }]
                }
              }]
            }
            """);
        var server = StudioServerPayload.Read(json.RootElement);
        var loader = new ViewerDocumentLoader();
        var workspace = new StudioWorkspace();
        var package = new StudioDraftPackage(
            StudioDraftPackage.CurrentSchemaVersion,
            "draft-server",
            "server:7",
            "Server",
            server.PgnText,
            ["server.pgn"],
            "server-game-0-g7",
            "g7.3",
            server.TranslationLinks,
            server.GameIdentities,
            7,
            "training",
            string.Empty,
            0,
            DateTimeOffset.UtcNow);

        await workspace.RestoreAsync(package, loader, token);

        var game = Assert.Single(workspace.Session.Games).Game;
        Assert.Equal("server-game-0-g7", game.Id);
        Assert.Equal("g7", game.Root.StableId);
        Assert.Equal("g7.3", Assert.Single(game.Root.Children).StableId);
        Assert.Contains("!?", Assert.Single(game.Root.Children).Annotations);
        Assert.Contains(5, Assert.Single(game.Root.Children).Nags);
        Assert.Equal("g7.3", workspace.Session.CurrentNode?.StableId);
    }

    [Fact]
    public async Task MarkedGamesAreDeletedTogetherWithoutChangingTheRemainingGame()
    {
        var token = TestContext.Current.CancellationToken;
        var loaded = await new ViewerDocumentLoader().LoadTextAsync(
            "[Event \"One\"]\n\n1. e4 *\n\n[Event \"Two\"]\n\n1. d4 *\n\n[Event \"Three\"]\n\n1. c4 *",
            "three-games.pgn",
            token);
        var workspace = new StudioWorkspace();
        workspace.Replace(loaded.Sources);
        workspace.Session.Games[0].IsMarked = true;
        workspace.Session.Games[2].IsMarked = true;

        var removed = workspace.RemoveMarkedGames();

        Assert.Equal(2, removed);
        var remaining = Assert.Single(workspace.Session.Games);
        Assert.Equal("Two", remaining.Game.Header("Event"));
        Assert.True(workspace.IsDirty);
    }

    [Fact]
    public async Task ServerPayloadKeepsBlackMoveNumberFromFenInsteadOfRebasingToPlyOne()
    {
        var token = TestContext.Current.CancellationToken;
        const string source = """
            [SetUp "1"]
            [FEN "8/8/8/8/8/6k1/8/4K3 b - - 0 19"]
            [Result "*"]

            19... Kf3 20. Kf1 *
            """;
        var loader = new ViewerDocumentLoader();
        var loaded = await loader.LoadTextAsync(source, "black-number.pgn", token);
        var workspace = new StudioWorkspace();
        workspace.Replace(loaded.Sources);

        var payload = workspace.BuildServerPayload();
        var first = payload.GetProperty("games")[0].GetProperty("root").GetProperty("children")[0];
        var rendered = StudioServerPayload.ToPgn(payload);

        Assert.False(first.GetProperty("isWhiteMove").GetBoolean());
        Assert.Equal(19, first.GetProperty("fullmoveNumber").GetInt32());
        Assert.Contains("19... Kf3 20. Kf1", rendered);

        using var legacyPayload = JsonDocument.Parse("""
            {"games":[{"headers":{"SetUp":"1","FEN":"8/8/8/8/8/6k1/8/4K3 b - - 0 19","Result":"*"},
            "root":{"id":"g0","san":"","uci":"","ply":0,
            "fen":"8/8/8/8/8/6k1/8/4K3 b - - 0 19","children":[
            {"id":"g0.0","san":"Kf3","uci":"g3f3","ply":1,
            "fen":"8/8/8/8/8/5k2/8/4K3 w - - 1 20","forceMoveNumber":true,"children":[
            {"id":"g0.0.0","san":"Kf1","uci":"e1f1","ply":2,
            "fen":"8/8/8/8/8/5k2/8/5K2 b - - 2 20","children":[]}]}]}}]}
            """);
        Assert.Contains("19... Kf3 20. Kf1", StudioServerPayload.ToPgn(legacyPayload.RootElement));
    }

    [Fact]
    public async Task ApprovedTranslationPropagatesToEveryLinkedOccurrenceInTheWorkspace()
    {
        var token = TestContext.Current.CancellationToken;
        const string sourceText = "White keeps the initiative.";
        var loaded = await new ViewerDocumentLoader().LoadTextAsync(
            $"1. e4 {{{sourceText}}} e5 {{{sourceText}}} *",
            "translation-links.pgn",
            token);
        var workspace = new StudioWorkspace();
        workspace.Replace(loaded.Sources);
        foreach (var item in workspace.CollectTranslationWork())
        {
            Assert.True(workspace.ApplyTranslation(new TranslationApplied(
                item,
                PhraseIdentity.Create(item.SourceText),
                "ترجمه اولیه",
                "server")));
        }

        var changed = workspace.ApplyTranslationMemoryUpdate(
            PhraseIdentity.Create(sourceText),
            sourceText,
            "ترجمه تأییدشده");

        Assert.Equal(2, changed);
        var comments = Assert.Single(workspace.Session.Games).Game.Root.Descendants()
            .Select(PgnTreeEditor.CommentText)
            .Where(static text => text.Length > 0)
            .ToArray();
        Assert.Equal(["ترجمه تأییدشده", "ترجمه تأییدشده"], comments);
    }

    [Fact]
    public async Task ServerDraftExtensionPreservesOrderedDuplicateHeaders()
    {
        var token = TestContext.Current.CancellationToken;
        var loaded = await new ViewerDocumentLoader().LoadTextAsync(
            "[Annotator \"First\"]\n[Annotator \"Second\"]\n[Result \"*\"]\n\n1. e4 *",
            "duplicate-headers.pgn",
            token);
        var workspace = new StudioWorkspace();
        workspace.Replace(loaded.Sources);

        var rendered = StudioServerPayload.ToPgn(workspace.BuildServerPayload());

        Assert.Contains("[Annotator \"First\"]\n[Annotator \"Second\"]", rendered.Replace("\r\n", "\n"));
    }

    [Fact]
    public void PendingAudioUsesStableGameIdentityAfterMultiGameReindex()
    {
        var identities = new[]
        {
            new PgnExternalGameIdentity("game-b", new PgnExternalNodeIdentity("root-b", [])),
            new PgnExternalGameIdentity("game-c", new PgnExternalNodeIdentity("root-c", [])),
        };

        Assert.Equal(0, StudioAudioIdentity.ResolveGameIndex(identities, "game-b", fallbackIndex: 1));
        Assert.Equal(1, StudioAudioIdentity.ResolveGameIndex(identities, "game-c", fallbackIndex: 2));
        Assert.Equal(-1, StudioAudioIdentity.ResolveGameIndex(identities, "game-a", fallbackIndex: 0));
        Assert.Equal(7, StudioAudioIdentity.ResolveGameIndex(null, "legacy-game", fallbackIndex: 7));

        var flat = new[]
        {
            new PgnFlatGameIdentity("game-c", [new PgnFlatNodeIdentity(0, "root-c", 0)]),
        };
        Assert.Equal(0, StudioAudioIdentity.ResolveGameIndex(
            null, "game-c", fallbackIndex: 2, flatGameIdentities: flat));
        Assert.Equal(-1, StudioAudioIdentity.ResolveGameIndex(
            null, "game-b", fallbackIndex: 0, flatGameIdentities: flat));
    }
}
