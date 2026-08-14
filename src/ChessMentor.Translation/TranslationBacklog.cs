using System.Text.Json;
using ChessMentor.Core;
using ChessMentor.Persistence;

namespace ChessMentor.Translation;

public sealed record PendingTranslationRequest(
    int SchemaVersion,
    IReadOnlyList<TranslationWorkItem> Items)
{
    public const int CurrentSchemaVersion = 1;
}

/// <summary>
/// Persists only transiently failed provider work. Cached and successful partial
/// results are never requested again; each source/course/target identity has one
/// idempotent queue row.
/// </summary>
public sealed class TranslationBacklog(SyncQueueRepository syncQueue)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<int> EnqueueFailuresAsync(
        IReadOnlyList<TranslationFailure> failures,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(failures);
        var groups = failures.Where(static failure => failure.Transient)
            .SelectMany(static failure => failure.Items)
            .GroupBy(static item => new
            {
                item.CourseId,
                item.PhraseIdentity,
                item.SourceLanguage,
                item.TargetLanguage,
            })
            .ToArray();
        foreach (var group in groups)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pending = new PendingTranslationRequest(
                PendingTranslationRequest.CurrentSchemaVersion,
                group.ToArray());
            var id = StableId.Create(
                "translation_request",
                group.Key.CourseId,
                group.Key.PhraseIdentity,
                group.Key.SourceLanguage,
                group.Key.TargetLanguage);
            await syncQueue.EnqueueAsync(
                id,
                "translation-request",
                "translation",
                group.Key.PhraseIdentity,
                JsonSerializer.Serialize(pending, JsonOptions),
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        return groups.Length;
    }

    public static bool IsTranslationRequest(SyncQueueItem item) =>
        string.Equals(item.OperationType, "translation-request", StringComparison.Ordinal);

    public static PendingTranslationRequest Deserialize(SyncQueueItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var pending = JsonSerializer.Deserialize<PendingTranslationRequest>(item.PayloadJson, JsonOptions)
            ?? throw new InvalidDataException("Pending translation request is invalid.");
        if (pending.SchemaVersion > PendingTranslationRequest.CurrentSchemaVersion || pending.Items.Count == 0)
        {
            throw new InvalidDataException("Pending translation request version or content is invalid.");
        }

        return pending;
    }
}
