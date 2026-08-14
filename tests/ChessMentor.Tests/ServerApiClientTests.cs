using System.Net;
using System.Text;
using ChessMentor.ServerClient;

namespace ChessMentor.Tests;

public sealed class ServerApiClientTests
{
    [Fact]
    public async Task SessionCsrfIsAppliedToTheNextMutation()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var call = 0;
        var logoutCsrf = string.Empty;
        using var handler = new RecordingHandler((request, _) =>
        {
            call++;
            if (call == 1)
            {
                Assert.Equal("/api/auth/session", request.RequestUri?.AbsolutePath);
                return Json("""{"authenticated":true,"needsSetup":false,"user":{"id":7},"csrfToken":"csrf-1","publicAccessEnabled":true}""");
            }

            Assert.Equal("/api/auth/logout", request.RequestUri?.AbsolutePath);
            logoutCsrf = request.Headers.GetValues("X-CSRF-Token").Single();
            return Json("""{"ok":true}""");
        });
        using var client = new ServerApiClient(new HttpClient(handler), new Uri("https://example.test"));

        var session = await client.GetSessionAsync(cancellationToken);
        await client.LogoutAsync(cancellationToken);

        Assert.True(session.Authenticated);
        Assert.Equal("csrf-1", logoutCsrf);
    }

    [Fact]
    public async Task TranslationUsesCurrentCommentsEndpointAndPreservesPhraseIdentity()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        string? requestJson = null;
        using var handler = new RecordingHandler(async (request, cancellationToken) =>
        {
            Assert.Equal("/api/translate-comments", request.RequestUri?.AbsolutePath);
            requestJson = await request.Content!.ReadAsStringAsync(cancellationToken);
            return Json("""
                {
                  "translations":["ترجمه"],
                  "translated":1,
                  "provider":"primary",
                  "model":"model",
                  "providersTried":["primary"],
                  "memoryHits":0,
                  "providerTranslated":1,
                  "deduplicated":0,
                  "memoryStored":1
                }
                """);
        });
        using var client = new ServerApiClient(new HttpClient(handler), new Uri("https://example.test"));

        var result = await client.TranslateAsync(new TranslationRequest(
            "source",
            "en",
            "fa",
            "phrase-42",
            CourseId: "course",
            GameId: "game",
            NodeId: "node"), cancellationToken);

        Assert.Equal("phrase-42", result.PhraseIdentity);
        Assert.Equal("ترجمه", result.TranslatedText);
        var payload = Assert.IsType<string>(requestJson);
        Assert.Contains("\"comments\":[\"source\"]", payload);
    }

    [Fact]
    public async Task TranslationPreflightIsExhaustiveBeforeProviderOnlyBatch()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var calls = 0;
        var providerOnlyConfirmed = false;
        using var handler = new RecordingHandler(async (request, cancellationToken) =>
        {
            calls++;
            if (calls == 1)
            {
                Assert.Equal("/api/translation-memory/preflight", request.RequestUri?.AbsolutePath);
                var body = await request.Content!.ReadAsStringAsync(cancellationToken);
                Assert.Contains("\"comments\":[\"White moves.\"]", body);
                return Json("""
                    {
                      "translations":[null],
                      "keys":["aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"],
                      "total":1,"matched":0,"missing":1,
                      "uniqueTotal":1,"uniqueMatched":0,"uniqueMissing":1,"duplicates":0,
                      "databaseFile":"memory.sqlite3","databaseEntries":7,
                      "enabled":true,"exhaustive":true
                    }
                    """);
            }

            Assert.Equal("/api/translate-comments", request.RequestUri?.AbsolutePath);
            var translationBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            providerOnlyConfirmed = translationBody.Contains("\"memoryPreflightConfirmed\":true", StringComparison.Ordinal);
            return Json("""
                {
                  "translations":["سفید حرکت می‌کند."],"translated":1,
                  "provider":"primary","model":"model","providersTried":["primary"],
                  "memoryHits":0,"providerTranslated":1,"deduplicated":0,"memoryStored":1
                }
                """);
        });
        using var client = new ServerApiClient(new HttpClient(handler), new Uri("https://example.test"));

        var preflight = await client.PreflightAsync(["White moves."], cancellationToken);
        var translated = await client.TranslateManyAsync(
            [new TranslationRequest("White moves.", "en", "fa", preflight.Keys[0])],
            new TranslationBatchOptions(MemoryPreflightConfirmed: true),
            cancellationToken);

        Assert.True(preflight.Exhaustive);
        Assert.True(providerOnlyConfirmed);
        Assert.Equal("سفید حرکت می‌کند.", Assert.Single(translated).TranslatedText);
    }

    [Fact]
    public async Task StudioDraftUsesTheExistingFastApiContract()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        string? requestJson = null;
        using var handler = new RecordingHandler(async (request, cancellationToken) =>
        {
            Assert.Equal("/api/drafts", request.RequestUri?.AbsolutePath);
            requestJson = await request.Content!.ReadAsStringAsync(cancellationToken);
            return Json("""{"ok":true,"draft":{"id":31}}""");
        });
        using var client = new ServerApiClient(new HttpClient(handler), new Uri("https://example.test"));
        var payload = System.Text.Json.JsonSerializer.SerializeToElement(new
        {
            games = new[] { new { root = new { id = "g0", children = Array.Empty<object>() } } },
        });

        var response = await client.SaveStudioDraftAsync(
            new StudioDraftRequest(
                "عنوان",
                "training",
                payload,
                "source.pgn",
                31,
                2500,
                "data:image/png;base64,iVBORw0KGgo="),
            cancellationToken);

        Assert.Equal(31, response.GetProperty("draft").GetProperty("id").GetInt64());
        var body = Assert.IsType<string>(requestJson);
        Assert.Contains("\"categorySlug\":\"training\"", body);
        Assert.Contains("\"sourceFile\":\"source.pgn\"", body);
        Assert.Contains("\"draftId\":31", body);
        Assert.Contains("\"creditPriceMinor\":2500", body);
        Assert.Contains("\"featuredImageData\":\"data:image/png;base64,iVBORw0KGgo=\"", body);
    }

    [Fact]
    public async Task MoveAudioUploadUsesNativeMultipartContract()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = Path.Combine(Path.GetTempPath(), "ChessMentor.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "move.wav");
        await File.WriteAllBytesAsync(path, "RIFF0000WAVEdata"u8.ToArray(), cancellationToken);
        string? requestBody = null;
        string? contentType = null;
        using var handler = new RecordingHandler(async (request, cancellationToken) =>
        {
            Assert.Equal("/api/course-workspaces/9/audio", request.RequestUri?.AbsolutePath);
            contentType = request.Content?.Headers.ContentType?.MediaType;
            requestBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            return Json("""
                {
                  "ok":true,
                  "audio":{"id":71,"courseId":9,"gameIndex":2,"nodeId":"g2.4",
                  "scope":"course","isMine":true,"mimeType":"audio/wav",
                  "durationMs":1250,"updatedAt":123,"url":"/api/move-audio/71"}
                }
                """);
        });
        using var client = new ServerApiClient(new HttpClient(handler), new Uri("https://example.test"));

        var result = await client.UploadMoveAudioAsync(
            9,
            2,
            "g2.4",
            "course",
            path,
            "audio/wav",
            1250,
            cancellationToken);

        Assert.Equal(71, result.Id);
        Assert.Equal("multipart/form-data", contentType);
        var body = Assert.IsType<string>(requestBody);
        Assert.Contains("gameIndex", body);
        Assert.Contains("nodeId", body);
        Assert.Contains("g2.4", body);
        Assert.Contains("scope", body);
        Assert.Contains("course", body);
    }

    private static HttpResponseMessage Json(string content) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(content, Encoding.UTF8, "application/json"),
    };

    private sealed class RecordingHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> callback) : HttpMessageHandler
    {
        public RecordingHandler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> callback)
            : this((request, cancellationToken) => Task.FromResult(callback(request, cancellationToken)))
        {
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => callback(request, cancellationToken);
    }
}
