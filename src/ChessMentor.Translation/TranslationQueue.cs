using System.Collections.Concurrent;
using ChessMentor.Persistence;
using ChessMentor.ServerClient;

namespace ChessMentor.Translation;

public sealed record TranslationWorkItem(
    string PhraseIdentity,
    string SourceText,
    string SourceLanguage,
    string TargetLanguage,
    string? CourseId,
    string? GameId,
    string? NodeId,
    string Field);

public sealed record TranslationQueueOptions(
    int Concurrency = 3,
    int BatchSize = 6,
    int MaxRetries = 2,
    TimeSpan? RetryBaseDelay = null,
    int MaxBatchCharacters = 58_000)
{
    public int SafeConcurrency => Math.Clamp(Concurrency, 1, 12);
    public int SafeBatchSize => Math.Clamp(BatchSize, 1, 12);
    public int SafeMaxRetries => Math.Clamp(MaxRetries, 0, 5);
    public TimeSpan SafeRetryBaseDelay => RetryBaseDelay ?? TimeSpan.FromMilliseconds(350);
    public int SafeMaxBatchCharacters => Math.Clamp(MaxBatchCharacters, 1, 60_000);
}

public sealed record TranslationApplied(
    TranslationWorkItem Item,
    string SourceHash,
    string TranslatedText,
    string Origin);

public sealed record TranslationFailure(
    IReadOnlyList<TranslationWorkItem> Items,
    string Message,
    bool Transient);

public sealed record TranslationQueueProgress(
    int Total,
    int Completed,
    int CacheHits,
    int ServerMemoryHits,
    int ServerTranslated,
    int Failed,
    string Message,
    TranslationApplied? Applied = null)
{
    public int Percentage => Total == 0
        ? 100
        : (int)Math.Round(Math.Min(Total, Completed + Failed) * 100d / Total);
}

public sealed record TranslationQueueResult(
    IReadOnlyList<TranslationApplied> Applied,
    IReadOnlyList<TranslationFailure> Failures,
    int CacheHits,
    int ServerMemoryHits,
    int ServerTranslated)
{
    public bool IsComplete => Failures.Count == 0;
}

public sealed class TranslationQueue(
    ITranslationApi server,
    TranslationCacheRepository cache)
{
    public async Task<TranslationQueueResult> RunAsync(
        IReadOnlyList<TranslationWorkItem> workItems,
        TranslationQueueOptions options,
        IProgress<TranslationQueueProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workItems);
        ArgumentNullException.ThrowIfNull(options);
        if (workItems.Count == 0)
        {
            progress?.Report(new TranslationQueueProgress(0, 0, 0, 0, 0, 0, "متنی برای ترجمه وجود ندارد."));
            return new TranslationQueueResult([], [], 0, 0, 0);
        }

        var groups = workItems.GroupBy(static item => item.PhraseIdentity, StringComparer.Ordinal)
            .Select(static group => new WorkGroup(group.Key, group.First().SourceText, group.ToArray()))
            .ToArray();
        var applied = new ConcurrentQueue<TranslationApplied>();
        var failures = new ConcurrentQueue<TranslationFailure>();
        var total = workItems.Count;
        var completed = 0;
        var failed = 0;
        var cacheHits = 0;
        var memoryHits = 0;
        var serverTranslated = 0;

        await cache.UpsertUsagesAsync(
            workItems.Where(static item => item.GameId is not null && item.NodeId is not null)
                .Select(static item => new TranslationCacheUsage(
                    item.PhraseIdentity,
                    item.TargetLanguage,
                    item.CourseId,
                    item.GameId!,
                    item.NodeId!,
                    item.Field,
                    DateTimeOffset.UtcNow))
                .ToArray(),
            cancellationToken).ConfigureAwait(false);

        void Report(string message, TranslationApplied? item = null) =>
            progress?.Report(new TranslationQueueProgress(
                total,
                Volatile.Read(ref completed),
                Volatile.Read(ref cacheHits),
                Volatile.Read(ref memoryHits),
                Volatile.Read(ref serverTranslated),
                Volatile.Read(ref failed),
                message,
                item));

        Report("در حال بررسی Cache محلی…");
        var cached = await cache.GetManyAsync(
            groups.Select(static group => group.Identity).ToArray(),
            workItems[0].TargetLanguage,
            cancellationToken).ConfigureAwait(false);
        var missing = new List<WorkGroup>();
        foreach (var group in groups)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!cached.TryGetValue(group.Identity, out var entry) || string.IsNullOrWhiteSpace(entry.TranslatedText))
            {
                missing.Add(group);
                continue;
            }

            foreach (var occurrence in group.Items)
            {
                var result = new TranslationApplied(occurrence, group.Identity, entry.TranslatedText, "local-cache");
                applied.Enqueue(result);
                Interlocked.Increment(ref completed);
                Interlocked.Increment(ref cacheHits);
                Report("ترجمه Cache محلی اعمال شد.", result);
            }
        }

        if (missing.Count == 0)
        {
            return BuildResult(applied, failures, cacheHits, memoryHits, serverTranslated);
        }

        TranslationPoolConfiguration configuration;
        TranslationMemoryPreflight preflight;
        try
        {
            Report("در حال پیش‌بررسی کامل Translation Memory سرور…");
            var configurationTask = server.GetConfigurationAsync(cancellationToken);
            var preflightTask = server.PreflightAsync(
                missing.Select(static group => group.SourceText).ToArray(),
                cancellationToken);
            await Task.WhenAll(configurationTask, preflightTask).ConfigureAwait(false);
            configuration = await configurationTask.ConfigureAwait(false);
            preflight = await preflightTask.ConfigureAwait(false);
            ValidatePreflight(preflight, missing.Count);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            var transient = IsTransient(exception);
            foreach (var group in missing)
            {
                failures.Enqueue(new TranslationFailure(group.Items, exception.Message, transient));
                Interlocked.Add(ref failed, group.Items.Count);
            }

            Report("سرور در دسترس نیست؛ فقط ترجمه‌های Cache‌شده اعمال شدند.");
            return BuildResult(applied, failures, cacheHits, memoryHits, serverTranslated);
        }

        var providerMissing = new List<WorkGroup>();
        var rememberedGroups = new List<(WorkGroup Group, string SourceHash, string Translation)>();
        for (var index = 0; index < missing.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var group = missing[index];
            var sourceHash = preflight.Keys[index];
            var remembered = preflight.Translations[index];
            if (string.IsNullOrWhiteSpace(remembered))
            {
                providerMissing.Add(group with { ServerIdentity = sourceHash });
                continue;
            }

            rememberedGroups.Add((group, sourceHash, remembered));
        }

        if (rememberedGroups.Count > 0)
        {
            await cache.UpsertManyAsync(
                rememberedGroups.Select(static item =>
                    CreateCacheEntry(item.Group, item.SourceHash, item.Translation, "server-memory")).ToArray(),
                cancellationToken).ConfigureAwait(false);
        }

        foreach (var (group, sourceHash, remembered) in rememberedGroups)
        {
            foreach (var occurrence in group.Items)
            {
                var result = new TranslationApplied(occurrence, sourceHash, remembered, "server-memory");
                applied.Enqueue(result);
                Interlocked.Increment(ref completed);
                Interlocked.Increment(ref memoryHits);
                Report("ترجمه Translation Memory سرور اعمال شد.", result);
            }
        }

        if (providerMissing.Count == 0)
        {
            return BuildResult(applied, failures, cacheHits, memoryHits, serverTranslated);
        }

        var oversized = providerMissing.Where(group =>
            group.SourceText.Length > options.SafeMaxBatchCharacters).ToArray();
        foreach (var group in oversized)
        {
            failures.Enqueue(new TranslationFailure(
                group.Items,
                $"متن {group.SourceText.Length} نویسه دارد و از سقف بسته ترجمه بیشتر است.",
                Transient: false));
            Interlocked.Add(ref failed, group.Items.Count);
        }

        var providerReady = providerMissing.Except(oversized).ToArray();
        if (providerReady.Length == 0)
        {
            Report("ترجمه با نتیجه جزئی پایان یافت.");
            return BuildResult(applied, failures, cacheHits, memoryHits, serverTranslated);
        }

        var batchSize = Math.Min(options.SafeBatchSize, Math.Clamp(configuration.BatchSize, 1, 12));
        var batches = BuildBatches(providerReady, batchSize, options.SafeMaxBatchCharacters);
        var serverWorkers = Math.Max(1, configuration.WorkerCount);
        var workerCount = Math.Min(Math.Min(options.SafeConcurrency, serverWorkers), batches.Count);
        var nextBatch = -1;
        Report($"ترجمه با {workerCount} Worker آغاز شد…");

        async Task WorkerAsync(int workerIndex)
        {
            while (true)
            {
                var batchIndex = Interlocked.Increment(ref nextBatch);
                if (batchIndex >= batches.Count)
                {
                    return;
                }

                var batch = batches[batchIndex];
                try
                {
                    var requests = batch.Select(group => new TranslationRequest(
                        group.SourceText,
                        group.Items[0].SourceLanguage,
                        group.Items[0].TargetLanguage,
                        group.ServerIdentity ?? group.Identity,
                        group.Items[0].CourseId,
                        group.Items[0].GameId,
                        group.Items[0].NodeId)).ToArray();
                    var translated = await ExecuteWithRetryAsync(
                        () => server.TranslateManyAsync(
                            requests,
                            new TranslationBatchOptions(workerIndex, MemoryPreflightConfirmed: true),
                            cancellationToken),
                        options,
                        cancellationToken).ConfigureAwait(false);
                    if (translated.Count != batch.Count)
                    {
                        throw new InvalidDataException("تعداد پاسخ‌های ترجمه با Batch برابر نیست.");
                    }

                    var cacheEntries = new List<TranslationCacheEntry>(batch.Count);
                    var batchResults = new List<TranslationApplied>();
                    for (var resultIndex = 0; resultIndex < batch.Count; resultIndex++)
                    {
                        var group = batch[resultIndex];
                        var translatedText = translated[resultIndex].TranslatedText;
                        var sourceHash = group.ServerIdentity ?? translated[resultIndex].PhraseIdentity;
                        cacheEntries.Add(CreateCacheEntry(group, sourceHash, translatedText, "server"));
                        foreach (var occurrence in group.Items)
                        {
                            batchResults.Add(new TranslationApplied(
                                occurrence,
                                sourceHash,
                                translatedText,
                                "server"));
                        }
                    }

                    await cache.UpsertManyAsync(cacheEntries, cancellationToken).ConfigureAwait(false);
                    foreach (var result in batchResults)
                    {
                        applied.Enqueue(result);
                        Interlocked.Increment(ref completed);
                        Interlocked.Increment(ref serverTranslated);
                        Report("نتیجه جزئی ترجمه اعمال شد.", result);
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    var batchItems = batch.SelectMany(static group => group.Items).ToArray();
                    failures.Enqueue(new TranslationFailure(batchItems, exception.Message, IsTransient(exception)));
                    Interlocked.Add(ref failed, batchItems.Length);
                    Report("یک Batch ناموفق بود؛ Batchهای دیگر ادامه دارند.");
                }
            }
        }

        await Task.WhenAll(Enumerable.Range(0, workerCount).Select(WorkerAsync)).ConfigureAwait(false);
        Report(failures.IsEmpty ? "ترجمه کامل شد." : "ترجمه با نتیجه جزئی پایان یافت.");
        return BuildResult(applied, failures, cacheHits, memoryHits, serverTranslated);
    }

    private static async Task<IReadOnlyList<TranslationResult>> ExecuteWithRetryAsync(
        Func<Task<IReadOnlyList<TranslationResult>>> operation,
        TranslationQueueOptions options,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await operation().ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (attempt < options.SafeMaxRetries && IsTransient(exception))
            {
                var factor = Math.Pow(2, attempt);
                var delay = TimeSpan.FromMilliseconds(options.SafeRetryBaseDelay.TotalMilliseconds * factor);
                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                }
            }
        }
    }

    private static List<IReadOnlyList<WorkGroup>> BuildBatches(
        IReadOnlyList<WorkGroup> groups,
        int batchSize,
        int maxCharacters)
    {
        var batches = new List<IReadOnlyList<WorkGroup>>();
        for (var cursor = 0; cursor < groups.Count;)
        {
            var gameId = groups[cursor].Items[0].GameId;
            var batch = new List<WorkGroup>(batchSize);
            var characters = 0;
            while (cursor < groups.Count &&
                   batch.Count < batchSize &&
                   string.Equals(groups[cursor].Items[0].GameId, gameId, StringComparison.Ordinal) &&
                   characters + groups[cursor].SourceText.Length <= maxCharacters)
            {
                characters += groups[cursor].SourceText.Length;
                batch.Add(groups[cursor]);
                cursor++;
            }

            batches.Add(batch);
        }

        return batches;
    }

    private static TranslationCacheEntry CreateCacheEntry(
        WorkGroup group,
        string sourceHash,
        string translatedText,
        string status)
    {
        var first = group.Items[0];
        return new TranslationCacheEntry(
            sourceHash,
            first.SourceLanguage,
            first.TargetLanguage,
            group.SourceText,
            translatedText,
            status,
            first.CourseId,
            first.GameId,
            first.NodeId,
            null,
            DateTimeOffset.UtcNow);
    }

    private static void ValidatePreflight(TranslationMemoryPreflight response, int expected)
    {
        if (!response.Exhaustive || response.Translations.Count != expected || response.Keys.Count != expected)
        {
            throw new InvalidDataException("پاسخ پیش‌بررسی Translation Memory کامل نیست.");
        }

        if (response.Keys.Any(static key => key.Length != 64 || !key.All(Uri.IsHexDigit)))
        {
            throw new InvalidDataException("شناسه پایدار متن در پاسخ Translation Memory معتبر نیست.");
        }
    }

    private static bool IsTransient(Exception exception) => exception switch
    {
        ServerApiException serverException => serverException.IsTransient,
        HttpRequestException => true,
        TimeoutException => true,
        TaskCanceledException => true,
        _ => false,
    };

    private static TranslationQueueResult BuildResult(
        ConcurrentQueue<TranslationApplied> applied,
        ConcurrentQueue<TranslationFailure> failures,
        int cacheHits,
        int memoryHits,
        int serverTranslated) =>
        new(applied.ToArray(), failures.ToArray(), cacheHits, memoryHits, serverTranslated);

    private sealed record WorkGroup(
        string Identity,
        string SourceText,
        IReadOnlyList<TranslationWorkItem> Items,
        string? ServerIdentity = null);
}
