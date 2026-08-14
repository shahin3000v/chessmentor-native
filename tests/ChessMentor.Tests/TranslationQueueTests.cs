using System.Collections.Concurrent;
using System.Text.Json;
using System.Net;
using ChessMentor.Persistence;
using ChessMentor.ServerClient;
using ChessMentor.Translation;

namespace ChessMentor.Tests;

public sealed class TranslationQueueTests
{
    [Fact]
    public void PhraseIdentityMatchesServerNormalizationForCommonChessProse()
    {
        const string source = "  White\u00a0keeps the — initiative.  ";

        Assert.Equal("white keeps the - initiative.", PhraseIdentity.NormalizeSource(source));
        Assert.Equal(
            "fd248d318fff9be72fa42f3b9dbbeb770b92cb0e10d6c05723448a6ba479af0f",
            PhraseIdentity.Create("White keeps the initiative."));
        Assert.True(PhraseIdentity.ShouldTranslate("White keeps the initiative."));
        Assert.False(PhraseIdentity.ShouldTranslate("[%clk 0:12:04]"));
    }

    [Fact]
    public async Task OfflineRunAppliesCacheAndKeepsMissingWorkRecoverable()
    {
        var token = TestContext.Current.CancellationToken;
        await using var database = await CreateDatabaseAsync(token);
        var cache = new TranslationCacheRepository(database);
        var cachedSource = "White keeps the initiative.";
        var cachedIdentity = PhraseIdentity.Create(cachedSource);
        await cache.UpsertManyAsync(
            [new TranslationCacheEntry(
                cachedIdentity,
                "en",
                "fa",
                cachedSource,
                "سفید ابتکار عمل را حفظ می‌کند.",
                "server",
                null,
                "game-1",
                "node-1",
                null,
                DateTimeOffset.UtcNow)],
            token);
        var server = new FakeTranslationApi { Offline = true };
        var queue = new TranslationQueue(server, cache);
        var work = new[]
        {
            Item(cachedSource, "node-1"),
            Item("Black must defend the weak pawn.", "node-2"),
        };

        var result = await queue.RunAsync(work, new TranslationQueueOptions(), cancellationToken: token);

        var applied = Assert.Single(result.Applied);
        Assert.Equal("node-1", applied.Item.NodeId);
        Assert.Equal("local-cache", applied.Origin);
        Assert.Single(result.Failures);
        Assert.True(Assert.Single(result.Failures).Transient);
    }

    [Fact]
    public async Task QueueUsesServerMemoryThenTranslatesOnlyUniqueMissingPhrases()
    {
        var token = TestContext.Current.CancellationToken;
        await using var database = await CreateDatabaseAsync(token);
        var server = new FakeTranslationApi();
        var cache = new TranslationCacheRepository(database);
        var queue = new TranslationQueue(server, cache);
        var first = "White keeps the initiative.";
        var second = "Black must defend the weak pawn.";
        server.PreflightFactory = sources => new TranslationMemoryPreflight(
            ["ترجمه حافظه", null],
            sources.Select(PhraseIdentity.Create).ToArray(),
            2,
            1,
            1,
            2,
            1,
            1,
            0,
            "translation.sqlite3",
            10,
            true,
            true);
        server.TranslateFactory = requests => requests.Select(request => new TranslationResult(
            request.PhraseIdentity,
            request.Text,
            "ترجمه مدل",
            "server")).ToArray();
        var updates = new List<TranslationQueueProgress>();

        var result = await queue.RunAsync(
            [Item(first, "node-1"), Item(first, "node-3"), Item(second, "node-2")],
            new TranslationQueueOptions(Concurrency: 2, RetryBaseDelay: TimeSpan.Zero),
            new InlineProgress<TranslationQueueProgress>(updates.Add),
            token);

        Assert.True(result.IsComplete);
        Assert.Equal(3, result.Applied.Count);
        Assert.Equal(2, result.ServerMemoryHits);
        Assert.Equal(1, result.ServerTranslated);
        Assert.Single(server.TranslatedBatches);
        Assert.Equal(second, Assert.Single(Assert.Single(server.TranslatedBatches)).Text);
        Assert.Contains(updates, update => update.Applied?.Item.NodeId == "node-3");
        var usages = await cache.ListUsagesAsync(PhraseIdentity.Create(first), "fa", token);
        Assert.Equal(["node-1", "node-3"], usages.Select(static usage => usage.NodeId));
    }

    [Fact]
    public async Task TransientProviderFailureRetriesAndPersistsTheSuccessfulResult()
    {
        var token = TestContext.Current.CancellationToken;
        await using var database = await CreateDatabaseAsync(token);
        var cache = new TranslationCacheRepository(database);
        var server = new FakeTranslationApi();
        var attempts = 0;
        server.TranslateAsyncFactory = (requests, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            attempts++;
            if (attempts == 1)
            {
                throw new ServerApiException(HttpStatusCode.ServiceUnavailable, "/translate", "retry");
            }

            return Task.FromResult<IReadOnlyList<TranslationResult>>(requests.Select(request =>
                new TranslationResult(request.PhraseIdentity, request.Text, "ترجمه پایدار", "server")).ToArray());
        };

        var result = await new TranslationQueue(server, cache).RunAsync(
            [Item("White controls the open file.", "node-retry")],
            new TranslationQueueOptions(MaxRetries: 1, RetryBaseDelay: TimeSpan.Zero),
            cancellationToken: token);

        Assert.True(result.IsComplete);
        Assert.Equal(2, attempts);
        Assert.Equal("ترجمه پایدار", Assert.Single(result.Applied).TranslatedText);
        var identity = PhraseIdentity.Create("White controls the open file.");
        Assert.Equal("ترجمه پایدار", (await cache.GetManyAsync([identity], "fa", token))[identity].TranslatedText);
    }

    [Fact]
    public async Task NonTransientBatchFailureDoesNotDiscardOtherPartialResults()
    {
        var token = TestContext.Current.CancellationToken;
        await using var database = await CreateDatabaseAsync(token);
        var server = new FakeTranslationApi
        {
            TranslateAsyncFactory = (requests, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var request = Assert.Single(requests);
                if (request.Text.Contains("fails", StringComparison.Ordinal))
                {
                    throw new ServerApiException(HttpStatusCode.BadRequest, "/translate", "invalid");
                }

                return Task.FromResult<IReadOnlyList<TranslationResult>>(
                    [new TranslationResult(request.PhraseIdentity, request.Text, "نتیجه سالم", "server")]);
            },
        };
        var queue = new TranslationQueue(server, new TranslationCacheRepository(database));

        var result = await queue.RunAsync(
            [Item("This batch succeeds.", "node-ok"), Item("This batch fails.", "node-fail")],
            new TranslationQueueOptions(Concurrency: 2, BatchSize: 1, MaxRetries: 0),
            cancellationToken: token);

        Assert.False(result.IsComplete);
        Assert.Equal("node-ok", Assert.Single(result.Applied).Item.NodeId);
        var failure = Assert.Single(result.Failures);
        Assert.Equal("node-fail", Assert.Single(failure.Items).NodeId);
        Assert.False(failure.Transient);
    }

    [Fact]
    public async Task CancellationStopsAnInFlightProviderBatch()
    {
        var testToken = TestContext.Current.CancellationToken;
        await using var database = await CreateDatabaseAsync(testToken);
        var server = new FakeTranslationApi
        {
            TranslateAsyncFactory = async (_, cancellationToken) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return Array.Empty<TranslationResult>();
            },
        };
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(testToken);
        cancellation.CancelAfter(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new TranslationQueue(server, new TranslationCacheRepository(database)).RunAsync(
                [Item("White develops the knight.", "node-cancel")],
                new TranslationQueueOptions(),
                cancellationToken: cancellation.Token));
    }

    [Fact]
    public async Task TransientFailuresBecomeIdempotentDurableBacklogItems()
    {
        var token = TestContext.Current.CancellationToken;
        await using var database = await CreateDatabaseAsync(token);
        var sync = new SyncQueueRepository(database);
        var backlog = new TranslationBacklog(sync);
        var first = Item("White keeps the initiative.", "node-1") with { CourseId = "draft-7" };
        var second = first with { NodeId = "node-2" };
        var permanent = Item("This request is invalid.", "node-3") with { CourseId = "draft-7" };

        var queued = await backlog.EnqueueFailuresAsync(
            [
                new TranslationFailure([first, second], "offline", Transient: true),
                new TranslationFailure([permanent], "bad request", Transient: false),
            ],
            token);
        _ = await backlog.EnqueueFailuresAsync(
            [new TranslationFailure([first, second], "still offline", Transient: true)],
            token);

        Assert.Equal(1, queued);
        var row = Assert.Single(await sync.ReadyAsync(cancellationToken: token));
        Assert.True(TranslationBacklog.IsTranslationRequest(row));
        var pending = TranslationBacklog.Deserialize(row);
        Assert.Equal(["node-1", "node-2"], pending.Items.Select(static item => item.NodeId));
        Assert.Equal(1, await sync.CountAsync(token));
    }

    [Fact]
    public async Task ProviderBatchesRespectBothItemAndCharacterLimits()
    {
        var token = TestContext.Current.CancellationToken;
        await using var database = await CreateDatabaseAsync(token);
        var server = new FakeTranslationApi();
        var sources = new[]
        {
            "First source text.",
            "Second source text.",
            "Third source text.",
        };

        var result = await new TranslationQueue(server, new TranslationCacheRepository(database)).RunAsync(
            sources.Select((source, index) => Item(source, $"node-{index}")).ToArray(),
            new TranslationQueueOptions(
                Concurrency: 1,
                BatchSize: 12,
                RetryBaseDelay: TimeSpan.Zero,
                MaxBatchCharacters: 25),
            cancellationToken: token);

        Assert.True(result.IsComplete);
        Assert.Equal(3, server.TranslatedBatches.Count);
        Assert.All(server.TranslatedBatches, batch => Assert.True(batch.Sum(static item => item.Text.Length) <= 25));
    }

    private static TranslationWorkItem Item(string source, string nodeId) => new(
        PhraseIdentity.Create(source),
        source,
        "en",
        "fa",
        null,
        "game-1",
        nodeId,
        "comment");

    private static async Task<AppDatabase> CreateDatabaseAsync(CancellationToken token)
    {
        var directory = Path.Combine(Path.GetTempPath(), "ChessMentor.Tests", Guid.NewGuid().ToString("N"));
        var database = new AppDatabase(Path.Combine(directory, "translation.db"));
        await database.InitializeAsync(token);
        return database;
    }

    private sealed class InlineProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }

    private sealed class FakeTranslationApi : ITranslationApi
    {
        public bool Offline { get; init; }
        public Func<IReadOnlyList<string>, TranslationMemoryPreflight>? PreflightFactory { get; set; }
        public Func<IReadOnlyList<TranslationRequest>, IReadOnlyList<TranslationResult>>? TranslateFactory { get; set; }
        public Func<IReadOnlyList<TranslationRequest>, CancellationToken, Task<IReadOnlyList<TranslationResult>>>?
            TranslateAsyncFactory { get; set; }
        public ConcurrentQueue<IReadOnlyList<TranslationRequest>> TranslatedBatches { get; } = new();

        public Task<TranslationPoolConfiguration> GetConfigurationAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Offline)
            {
                throw new HttpRequestException("offline");
            }

            return Task.FromResult(new TranslationPoolConfiguration(
                [new TranslationProviderInfo("test", "model")],
                2,
                6,
                120,
                60,
                1,
                new TranslationGlossaryInfo(true, 501, "1.0.1"),
                new TranslationMemoryInfo(true, true, 10),
                "test"));
        }

        public Task<TranslationMemoryPreflight> PreflightAsync(
            IReadOnlyList<string> sourceTexts,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Offline)
            {
                throw new HttpRequestException("offline");
            }

            return Task.FromResult(PreflightFactory?.Invoke(sourceTexts) ?? new TranslationMemoryPreflight(
                sourceTexts.Select(static _ => (string?)null).ToArray(),
                sourceTexts.Select(PhraseIdentity.Create).ToArray(),
                sourceTexts.Count,
                0,
                sourceTexts.Count,
                sourceTexts.Count,
                0,
                sourceTexts.Count,
                0,
                string.Empty,
                0,
                false,
                true));
        }

        public async Task<TranslationResult> TranslateAsync(
            TranslationRequest request,
            CancellationToken cancellationToken = default) =>
            Assert.Single(await TranslateManyAsync([request], cancellationToken: cancellationToken));

        public Task<IReadOnlyList<TranslationResult>> TranslateManyAsync(
            IReadOnlyList<TranslationRequest> requests,
            TranslationBatchOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TranslatedBatches.Enqueue(requests);
            if (TranslateAsyncFactory is not null)
            {
                return TranslateAsyncFactory(requests, cancellationToken);
            }

            return Task.FromResult(TranslateFactory?.Invoke(requests) ?? requests.Select(request =>
                new TranslationResult(request.PhraseIdentity, request.Text, request.Text, "server")).ToArray());
        }

        public Task<JsonElement> UpdateTranslationMemoryAsync(
            string sourceHash,
            string sourceText,
            string translationText,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(JsonSerializer.SerializeToElement(new { ok = true }));
    }
}
