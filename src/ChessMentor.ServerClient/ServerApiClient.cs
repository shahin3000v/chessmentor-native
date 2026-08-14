using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace ChessMentor.ServerClient;

public sealed class ServerApiClient : IServerApi, IAuthApi, ITranslationApi, IStudioApi, IMoveAudioApi, ICourseSyncApi, IContributionApi, IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly JsonSerializerOptions _json;
    private string _csrfToken = string.Empty;

    public ServerApiClient(HttpClient httpClient, Uri baseAddress, JsonSerializerOptions? serializerOptions = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(baseAddress);
        _httpClient = httpClient;
        _httpClient.BaseAddress = baseAddress;
        _json = serializerOptions ?? new JsonSerializerOptions(JsonSerializerDefaults.Web);
    }

    public async Task<TResponse> SendAsync<TResponse>(
        HttpMethod method,
        string relativePath,
        object? body = null,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(method, relativePath);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: _json);
        }

        if (method != HttpMethod.Get && method != HttpMethod.Head && !string.IsNullOrEmpty(_csrfToken))
        {
            request.Headers.TryAddWithoutValidation("X-CSRF-Token", _csrfToken);
        }

        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var details = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new ServerApiException(response.StatusCode, relativePath, details);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync<TResponse>(stream, _json, cancellationToken).ConfigureAwait(false)
            ?? throw new JsonException($"Server returned an empty response for {relativePath}.");
    }

    public async Task<AuthSessionResponse> GetSessionAsync(CancellationToken cancellationToken = default)
    {
        var response = await SendAsync<AuthSessionResponse>(
            HttpMethod.Get,
            "/api/auth/session",
            cancellationToken: cancellationToken).ConfigureAwait(false);
        _csrfToken = response.CsrfToken;
        return response;
    }

    public async Task<LoginResponse> LoginAsync(LoginRequest request, CancellationToken cancellationToken = default)
    {
        var response = await SendAsync<LoginResponse>(
            HttpMethod.Post,
            "/api/auth/login",
            request,
            cancellationToken).ConfigureAwait(false);
        _csrfToken = response.CsrfToken;
        return response;
    }

    public async Task LogoutAsync(CancellationToken cancellationToken = default)
    {
        _ = await SendAsync<ApiOkResponse>(HttpMethod.Post, "/api/auth/logout", cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        _csrfToken = string.Empty;
    }

    public async Task<TranslationResult> TranslateAsync(
        TranslationRequest request,
        CancellationToken cancellationToken = default)
    {
        var translated = await TranslateManyAsync([request], cancellationToken: cancellationToken).ConfigureAwait(false);
        return translated[0];
    }

    public Task<TranslationPoolConfiguration> GetConfigurationAsync(CancellationToken cancellationToken = default) =>
        SendAsync<TranslationPoolConfiguration>(
            HttpMethod.Get,
            "/api/translation-config",
            cancellationToken: cancellationToken);

    public Task<TranslationMemoryPreflight> PreflightAsync(
        IReadOnlyList<string> sourceTexts,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sourceTexts);
        if (sourceTexts.Count > 25_000)
        {
            throw new ArgumentOutOfRangeException(nameof(sourceTexts), "The server accepts at most 25,000 preflight strings.");
        }

        return SendAsync<TranslationMemoryPreflight>(
            HttpMethod.Post,
            "/api/translation-memory/preflight",
            new { comments = sourceTexts },
            cancellationToken);
    }

    public async Task<IReadOnlyList<TranslationResult>> TranslateManyAsync(
        IReadOnlyList<TranslationRequest> requests,
        TranslationBatchOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requests);
        if (requests.Count is < 1 or > 12)
        {
            throw new ArgumentOutOfRangeException(nameof(requests), "The current server accepts 1..12 comments per batch.");
        }

        var response = await SendAsync<TranslationCommentsResponse>(
            HttpMethod.Post,
            "/api/translate-comments",
            new
            {
                comments = requests.Select(static request => request.Text).ToArray(),
                preferredProviderIndex = options?.PreferredProviderIndex ?? 0,
                memoryPreflightConfirmed = options?.MemoryPreflightConfirmed ?? false,
            },
            cancellationToken).ConfigureAwait(false);
        if (response.Translations.Count != requests.Count)
        {
            throw new JsonException("Translation response count does not match the request count.");
        }

        return requests.Select((request, index) => new TranslationResult(
            request.PhraseIdentity,
            request.Text,
            response.Translations[index],
            "server",
            Metadata: response.Metadata())).ToArray();
    }

    public Task<JsonElement> UpdateTranslationMemoryAsync(
        string sourceHash,
        string sourceText,
        string translationText,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceHash);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceText);
        ArgumentException.ThrowIfNullOrWhiteSpace(translationText);
        return SendAsync<JsonElement>(
            HttpMethod.Put,
            $"/api/admin/translation-memory/source/{Uri.EscapeDataString(sourceHash)}",
            new { sourceText, translationText },
            cancellationToken);
    }

    public async Task<IReadOnlyList<StudioCategory>> GetCategoriesAsync(
        CancellationToken cancellationToken = default)
    {
        var response = await SendAsync<CategoriesResponse>(
            HttpMethod.Get,
            "/api/categories",
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return response.Categories;
    }

    public Task<JsonElement> GetDraftAsync(long draftId, CancellationToken cancellationToken = default)
    {
        if (draftId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(draftId));
        }

        return SendAsync<JsonElement>(
            HttpMethod.Get,
            $"/api/drafts/{draftId}",
            cancellationToken: cancellationToken);
    }

    public Task<JsonElement> SaveStudioDraftAsync(
        StudioDraftRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return SendAsync<JsonElement>(
            HttpMethod.Post,
            "/api/drafts",
            new
            {
                request.Title,
                request.CategorySlug,
                payload = request.Payload,
                request.SourceFile,
                request.DraftId,
                request.CreditPriceMinor,
                request.FeaturedImageData,
            },
            cancellationToken);
    }

    public Task<JsonElement> PublishCourseAsync(
        StudioPublishRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return SendAsync<JsonElement>(
            HttpMethod.Post,
            "/api/courses",
            new
            {
                request.Title,
                request.Slug,
                request.CategorySlug,
                payload = request.Payload,
                request.SourceFile,
                request.DraftId,
                request.CreditPriceMinor,
                request.FeaturedImageData,
            },
            cancellationToken);
    }

    public async Task<IReadOnlyList<MoveAudioItem>> ListMoveAudioAsync(
        long courseId,
        int gameIndex,
        CancellationToken cancellationToken = default)
    {
        ValidateAudioIdentity(courseId, gameIndex);
        var response = await SendAsync<MoveAudioListResponse>(
            HttpMethod.Get,
            $"/api/course-workspaces/{courseId}/audio?gameIndex={gameIndex}",
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return response.Audio;
    }

    public async Task<MoveAudioItem> UploadMoveAudioAsync(
        long courseId,
        int gameIndex,
        string nodeId,
        string scope,
        string filePath,
        string contentType,
        long durationMilliseconds,
        CancellationToken cancellationToken = default)
    {
        ValidateAudioIdentity(courseId, gameIndex);
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeId);
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        if (scope is not ("course" or "user"))
        {
            throw new ArgumentOutOfRangeException(nameof(scope));
        }

        await using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length is <= 0 or > 25 * 1024 * 1024)
        {
            throw new InvalidDataException("Move audio must contain 1..25 MB of data.");
        }

        using var content = new MultipartFormDataContent();
        using var audio = new StreamContent(stream);
        audio.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
        content.Add(audio, "audio", Path.GetFileName(filePath));
        content.Add(new StringContent(gameIndex.ToString(System.Globalization.CultureInfo.InvariantCulture)), "gameIndex");
        content.Add(new StringContent(nodeId), "nodeId");
        content.Add(new StringContent(Math.Clamp(durationMilliseconds, 0, 12L * 60 * 60 * 1000)
            .ToString(System.Globalization.CultureInfo.InvariantCulture)), "durationMs");
        content.Add(new StringContent(scope), "scope");
        var response = await SendContentAsync<MoveAudioUploadResponse>(
            HttpMethod.Post,
            $"/api/course-workspaces/{courseId}/audio",
            content,
            cancellationToken).ConfigureAwait(false);
        return response.Audio;
    }

    public async Task<byte[]> DownloadMoveAudioAsync(
        long audioId,
        CancellationToken cancellationToken = default)
    {
        if (audioId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(audioId));
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/move-audio/{audioId}");
        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var details = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new ServerApiException(response.StatusCode, request.RequestUri?.ToString() ?? string.Empty, details);
        }

        var declaredLength = response.Content.Headers.ContentLength;
        if (declaredLength is > 25 * 1024 * 1024)
        {
            throw new InvalidDataException("Server audio exceeds the 25 MB limit.");
        }

        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        return bytes.Length <= 25 * 1024 * 1024
            ? bytes
            : throw new InvalidDataException("Server audio exceeds the 25 MB limit.");
    }

    public async Task DeleteMoveAudioAsync(long audioId, CancellationToken cancellationToken = default)
    {
        if (audioId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(audioId));
        }

        _ = await SendAsync<ApiOkResponse>(
            HttpMethod.Delete,
            $"/api/move-audio/{audioId}",
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public Task<JsonElement> GetBuilderDocumentAsync(string courseId, CancellationToken cancellationToken = default) =>
        SendAsync<JsonElement>(HttpMethod.Get, $"/api/admin/course-builder/{Uri.EscapeDataString(courseId)}", cancellationToken: cancellationToken);

    public Task<JsonElement> PutBuilderDocumentAsync(
        string courseId,
        JsonElement document,
        int expectedRevision,
        CancellationToken cancellationToken = default) =>
        SendAsync<JsonElement>(
            HttpMethod.Put,
            $"/api/admin/course-builder/{Uri.EscapeDataString(courseId)}",
            new { document, expectedRevision },
            cancellationToken);

    public Task<JsonElement> SaveDraftAsync(JsonElement serverPayload, CancellationToken cancellationToken = default) =>
        SendAsync<JsonElement>(HttpMethod.Post, "/api/drafts", serverPayload, cancellationToken);

    public Task<JsonElement> GetMyContributionsAsync(CancellationToken cancellationToken = default) =>
        SendAsync<JsonElement>(HttpMethod.Get, "/api/translation-contributions/me", cancellationToken: cancellationToken);

    public Task<JsonElement> SubmitSuggestionAsync(JsonElement contribution, CancellationToken cancellationToken = default) =>
        SendAsync<JsonElement>(HttpMethod.Post, "/api/translation-suggestions", contribution, cancellationToken);

    public void Dispose() => _httpClient.Dispose();

    private async Task<TResponse> SendContentAsync<TResponse>(
        HttpMethod method,
        string relativePath,
        HttpContent content,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, relativePath) { Content = content };
        if (!string.IsNullOrEmpty(_csrfToken))
        {
            request.Headers.TryAddWithoutValidation("X-CSRF-Token", _csrfToken);
        }

        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var details = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new ServerApiException(response.StatusCode, relativePath, details);
        }

        await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync<TResponse>(
            responseStream,
            _json,
            cancellationToken).ConfigureAwait(false)
            ?? throw new JsonException($"Server returned an empty response for {relativePath}.");
    }

    private static void ValidateAudioIdentity(long courseId, int gameIndex)
    {
        if (courseId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(courseId));
        }

        if (gameIndex < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(gameIndex));
        }
    }

    private sealed record TranslationCommentsResponse(
        IReadOnlyList<string> Translations,
        int Translated,
        string Provider,
        string Model,
        IReadOnlyList<string> ProvidersTried,
        int MemoryHits,
        int ProviderTranslated,
        int Deduplicated,
        int MemoryStored)
    {
        public JsonElement Metadata() => JsonSerializer.SerializeToElement(new
        {
            Translated,
            Provider,
            Model,
            ProvidersTried,
            MemoryHits,
            ProviderTranslated,
            Deduplicated,
            MemoryStored,
        });
    }

    private sealed record CategoriesResponse(IReadOnlyList<StudioCategory> Categories);
    private sealed record MoveAudioListResponse(IReadOnlyList<MoveAudioItem> Audio);
    private sealed record MoveAudioUploadResponse(bool Ok, MoveAudioItem Audio);
}

public static class ServerClientFactory
{
    public static ServerApiClient Create(Uri baseAddress, TimeSpan? timeout = null)
    {
        var handler = new HttpClientHandler
        {
            UseCookies = true,
            CookieContainer = new CookieContainer(),
            AutomaticDecompression = DecompressionMethods.All,
        };
        var client = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = timeout ?? TimeSpan.FromMinutes(8),
        };
        return new ServerApiClient(client, baseAddress);
    }
}

public sealed class ServerApiException(HttpStatusCode statusCode, string path, string responseBody)
    : HttpRequestException($"Server request to {path} failed with {(int)statusCode} ({statusCode}).")
{
    public HttpStatusCode StatusCodeValue { get; } = statusCode;
    public string Path { get; } = path;
    public string ResponseBody { get; } = responseBody;
    public bool IsTransient => StatusCodeValue is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests ||
        (int)StatusCodeValue >= 500;
}
