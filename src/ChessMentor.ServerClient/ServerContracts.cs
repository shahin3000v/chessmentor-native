using System.Text.Json;

namespace ChessMentor.ServerClient;

public sealed record AuthSessionResponse(
    bool Authenticated,
    bool NeedsSetup,
    JsonElement? User,
    string CsrfToken,
    bool PublicAccessEnabled);

public sealed record LoginResponse(bool Ok, JsonElement User, string CsrfToken);

public sealed record ApiOkResponse(bool Ok);

public sealed record LoginRequest(string Identifier, string Password);

public sealed record TranslationRequest(
    string Text,
    string SourceLanguage,
    string TargetLanguage,
    string PhraseIdentity,
    string? CourseId = null,
    string? GameId = null,
    string? NodeId = null);

public sealed record TranslationResult(
    string PhraseIdentity,
    string SourceText,
    string TranslatedText,
    string Status,
    decimal CreditsUsed = 0,
    JsonElement? Metadata = null);

public sealed record TranslationBatchOptions(
    int PreferredProviderIndex = 0,
    bool MemoryPreflightConfirmed = false);

public sealed record TranslationProviderInfo(string Label, string ModelId);

public sealed record TranslationGlossaryInfo(bool Enabled, int EntryCount, string Version);

public sealed record TranslationMemoryInfo(
    bool Enabled,
    bool Available,
    int Entries,
    string DatabaseFile = "");

public sealed record TranslationPoolConfiguration(
    IReadOnlyList<TranslationProviderInfo> Providers,
    int WorkerCount,
    int BatchSize,
    int BatchRequestTimeoutSeconds,
    int ProviderTimeoutSeconds,
    int FailoverProviderLimit,
    TranslationGlossaryInfo Glossary,
    TranslationMemoryInfo TranslationMemory,
    string Version);

public sealed record TranslationMemoryPreflight(
    IReadOnlyList<string?> Translations,
    IReadOnlyList<string> Keys,
    int Total,
    int Matched,
    int Missing,
    int UniqueTotal,
    int UniqueMatched,
    int UniqueMissing,
    int Duplicates,
    string DatabaseFile,
    int DatabaseEntries,
    bool Enabled,
    bool Exhaustive);

public sealed record StudioCategory(string Name, string Slug);

public sealed record StudioDraftRequest(
    string Title,
    string CategorySlug,
    JsonElement Payload,
    string SourceFile,
    long? DraftId = null,
    int CreditPriceMinor = 0,
    string FeaturedImageData = "");

public sealed record StudioPublishRequest(
    string Title,
    string Slug,
    string CategorySlug,
    JsonElement Payload,
    string SourceFile,
    long? DraftId = null,
    int CreditPriceMinor = 0,
    string FeaturedImageData = "");

public sealed record MoveAudioItem(
    long Id,
    long CourseId,
    int GameIndex,
    string NodeId,
    string Scope,
    bool IsMine,
    string MimeType,
    long DurationMs,
    long UpdatedAt,
    string Url);

public interface IServerApi
{
    Task<TResponse> SendAsync<TResponse>(
        HttpMethod method,
        string relativePath,
        object? body = null,
        CancellationToken cancellationToken = default);
}

public interface IAuthApi
{
    Task<AuthSessionResponse> GetSessionAsync(CancellationToken cancellationToken = default);
    Task<LoginResponse> LoginAsync(LoginRequest request, CancellationToken cancellationToken = default);
    Task LogoutAsync(CancellationToken cancellationToken = default);
}

public interface ITranslationApi
{
    Task<TranslationPoolConfiguration> GetConfigurationAsync(CancellationToken cancellationToken = default);
    Task<TranslationMemoryPreflight> PreflightAsync(
        IReadOnlyList<string> sourceTexts,
        CancellationToken cancellationToken = default);
    Task<TranslationResult> TranslateAsync(TranslationRequest request, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TranslationResult>> TranslateManyAsync(
        IReadOnlyList<TranslationRequest> requests,
        TranslationBatchOptions? options = null,
        CancellationToken cancellationToken = default);
    Task<JsonElement> UpdateTranslationMemoryAsync(
        string sourceHash,
        string sourceText,
        string translationText,
        CancellationToken cancellationToken = default);
}

public interface IStudioApi
{
    Task<IReadOnlyList<StudioCategory>> GetCategoriesAsync(CancellationToken cancellationToken = default);
    Task<JsonElement> GetDraftAsync(long draftId, CancellationToken cancellationToken = default);
    Task<JsonElement> SaveStudioDraftAsync(
        StudioDraftRequest request,
        CancellationToken cancellationToken = default);
    Task<JsonElement> PublishCourseAsync(
        StudioPublishRequest request,
        CancellationToken cancellationToken = default);
}

public interface IMoveAudioApi
{
    Task<IReadOnlyList<MoveAudioItem>> ListMoveAudioAsync(
        long courseId,
        int gameIndex,
        CancellationToken cancellationToken = default);
    Task<MoveAudioItem> UploadMoveAudioAsync(
        long courseId,
        int gameIndex,
        string nodeId,
        string scope,
        string filePath,
        string contentType,
        long durationMilliseconds,
        CancellationToken cancellationToken = default);
    Task<byte[]> DownloadMoveAudioAsync(long audioId, CancellationToken cancellationToken = default);
    Task DeleteMoveAudioAsync(long audioId, CancellationToken cancellationToken = default);
}

public interface ICourseSyncApi
{
    Task<JsonElement> GetBuilderDocumentAsync(string courseId, CancellationToken cancellationToken = default);
    Task<JsonElement> PutBuilderDocumentAsync(
        string courseId,
        JsonElement document,
        int expectedRevision,
        CancellationToken cancellationToken = default);
    Task<JsonElement> SaveDraftAsync(JsonElement serverPayload, CancellationToken cancellationToken = default);
}

public interface IContributionApi
{
    Task<JsonElement> GetMyContributionsAsync(CancellationToken cancellationToken = default);
    Task<JsonElement> SubmitSuggestionAsync(JsonElement contribution, CancellationToken cancellationToken = default);
}
