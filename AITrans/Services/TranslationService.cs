using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AITrans.Models;

namespace AITrans.Services;

public class TranslationService
{
    private static readonly HttpClient HttpClient = new(new SocketsHttpHandler
    {
        // Explicitly enable TLS 1.2 and 1.3 to avoid SSL handshake failures
        // on endpoints like api.x.ai that require modern TLS cipher suites.
        SslOptions = new System.Net.Security.SslClientAuthenticationOptions
        {
            EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls12
                | System.Security.Authentication.SslProtocols.Tls13
        },
        PooledConnectionLifetime = TimeSpan.FromMinutes(15),
    });
    private const int MaxRetries = 3;
    private const int LocalTranslationMinTokens = 512;
    private const int LocalTranslationMaxTokens = 4096;

    // Reasoning models can spend their entire token budget "thinking" (surfaced separately as
    // reasoning_content by some OpenAI-compatible servers, e.g. llama.cpp / DeepSeek) and never
    // reach the actual answer, leaving finish_reason=length with empty content. When that
    // signature is detected we retry once with a much larger budget before giving up.
    private const int ReasoningRetryMaxTokens = 8192;
    private const int ReasoningRetryDefaultTokens = 4096;
    private int _rotationIndex;

    /// <summary>
    /// Gets the next model to use, rotating through the list for OpenRouter auto-rotate.
    /// </summary>
    private string GetNextModel(AppSettings settings)
    {
        if (settings.Provider != AiProvider.OpenRouter || !settings.OpenRouterAutoRotate
            || settings.OpenRouterFreeModels.Count == 0)
        {
            return settings.ActiveModel;
        }

        var models = settings.OpenRouterFreeModels;
        var model = models[_rotationIndex % models.Count];
        _rotationIndex++;
        return model;
    }

    private static bool IsLocalTranslationProvider(AppSettings? settings, string endpoint) =>
        settings?.OllamaLmStudioEndpoint?.Equals(endpoint, StringComparison.OrdinalIgnoreCase) == true;

    private static string BuildLocalTranslationPrompt(string text, string targetLanguage) =>
        $"Translate the text to {targetLanguage}.\n" +
        "Return the result only inside <translation> and </translation>.\n" +
        "The content inside <translation> must be in the target language, not in the source language.\n" +
        "Rules:\n" +
        "- Preserve markdown, punctuation, spacing, and line breaks.\n" +
        "- Preserve special characters exactly.\n" +
        "- Do not add explanations, notes, or quotes.\n" +
        "- Do not write anything before or after the <translation> block.\n\n" +
        "Text:\n" +
        text;

    private static string BuildLocalTranslationRetryPrompt(string text, string targetLanguage) =>
        $"Translate this text to {targetLanguage}.\n" +
        "Answer in the target language only.\n" +
        "Do not explain.\n" +
        "Do not repeat the source language.\n\n" +
        text;

    private static int EstimateLocalTranslationMaxTokens(string prompt) =>
        Math.Clamp(prompt.Length / 2, LocalTranslationMinTokens, LocalTranslationMaxTokens);

    private static double GetLocalTranslationTemperature() => 0.1;

    private static string ExtractLocalTranslationContent(string response)
    {
        if (string.IsNullOrWhiteSpace(response))
            return "";

        var match = Regex.Match(response, @"<translation>\s*(.*?)\s*</translation>",
            RegexOptions.Singleline | RegexOptions.IgnoreCase);
        if (match.Success)
            return match.Groups[1].Value.Trim();

        var lines = response
            .Split('\n')
            .Select(l => l.TrimEnd('\r'))
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .ToList();

        var filtered = lines
            .Where(l => !Regex.IsMatch(l, @"^(Разбира се|Ето|Ако имаш|If you|Here is|Sure\b)", RegexOptions.IgnoreCase))
            .ToList();

        return string.Join("\n", filtered.Count > 0 ? filtered : lines).Trim();
    }

    private static string NormalizeForComparison(string text) =>
        Regex.Replace(text, @"\s+", " ").Trim().ToLowerInvariant();

    private static bool LooksUntranslated(string sourceText, string translatedText) =>
        !string.IsNullOrWhiteSpace(sourceText)
        && !string.IsNullOrWhiteSpace(translatedText)
        && NormalizeForComparison(sourceText) == NormalizeForComparison(translatedText);

    private async Task<string> TranslateLocalTextAsync(
        string text, string targetLanguage, string apiKey, string model, string endpoint,
        AppSettings? settings, CancellationToken ct)
    {
        var localPrompt = BuildLocalTranslationPrompt(text, targetLanguage);
        var response = await CallApiWithRetryAsync("", localPrompt, apiKey, model, endpoint, settings, ct);
        var extracted = ExtractLocalTranslationContent(response);

        if (!LooksUntranslated(text, extracted) && !string.IsNullOrWhiteSpace(extracted))
            return extracted;

        var retryPrompt = BuildLocalTranslationRetryPrompt(text, targetLanguage);
        var retryResponse = await CallApiWithRetryAsync("", retryPrompt, apiKey, model, endpoint, settings, ct);
        var retryExtracted = ExtractLocalTranslationContent(retryResponse);

        return string.IsNullOrWhiteSpace(retryExtracted) ? retryResponse.Trim() : retryExtracted;
    }

    // DeepL language code mapping
    private static string ToDeepLLang(string language) => language.ToLowerInvariant() switch
    {
        "bulgarian" => "BG",
        "russian" => "RU",
        "english" => "EN-US",
        "german" => "DE",
        "french" => "FR",
        "spanish" => "ES",
        _ => language.ToUpperInvariant()
    };

    /// <summary>
    /// Translate a batch of texts using the DeepL API.
    /// Each paragraph is sent individually so we get paragraph-level progress callbacks.
    /// </summary>
    public async Task<List<string>> TranslateDeepLBatchAsync(
        List<string> texts, string targetLanguage, string apiKey, bool freeApi,
        IProgress<int>? progress = null, Action<int, string>? onEntryTranslated = null,
        CancellationToken ct = default, int delayBetweenRequestsMs = 0)
    {
        var endpoint = freeApi
            ? "https://api-free.deepl.com/v2/translate"
            : "https://api.deepl.com/v2/translate";
        var langCode = ToDeepLLang(targetLanguage);
        var results = new string[texts.Count];

        for (int i = 0; i < texts.Count; i++)
        {
            ct.ThrowIfCancellationRequested();

            // Pause every 10 requests to stay within DeepL rate limits
            if (i > 0 && i % 10 == 0 && delayBetweenRequestsMs > 0)
                await Task.Delay(delayBetweenRequestsMs, ct);

            var payload = new
            {
                text = new[] { texts[i] },
                target_lang = langCode
            };
            var body = System.Text.Json.JsonSerializer.Serialize(payload);

            using var req = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("DeepL-Auth-Key", apiKey);

            using var resp = await HttpClient.SendAsync(req, ct);
            var json = await resp.Content.ReadAsStringAsync(ct);

            if (!resp.IsSuccessStatusCode)
                throw new HttpRequestException($"DeepL error {resp.StatusCode}: {json}");

            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var translated = doc.RootElement
                .GetProperty("translations")[0]
                .GetProperty("text")
                .GetString() ?? "";

            results[i] = translated;
            onEntryTranslated?.Invoke(i, translated);
            progress?.Report((i + 1) * 100 / texts.Count);
        }

        return [.. results];
    }

    // Google Translate (free, unofficial translate_a/single endpoint) language code mapping
    private static string ToGoogleTranslateLang(string language) => language.ToLowerInvariant() switch
    {
        "bulgarian" => "bg",
        "russian" => "ru",
        "english" => "en",
        "german" => "de",
        "french" => "fr",
        "spanish" => "es",
        _ => language.ToLowerInvariant()
    };

    /// <summary>
    /// Translate a batch of texts using the free, unofficial Google Translate endpoint
    /// (translate.googleapis.com/translate_a/single). No API key required, but this endpoint
    /// is undocumented, rate-limited, and may change or block requests without notice.
    /// Each text is sent individually so we get paragraph-level progress callbacks.
    /// </summary>
    public async Task<List<string>> TranslateGoogleFreeBatchAsync(
        List<string> texts, string targetLanguage,
        IProgress<int>? progress = null, Action<int, string>? onEntryTranslated = null,
        CancellationToken ct = default, int delayBetweenRequestsMs = 0)
    {
        var langCode = ToGoogleTranslateLang(targetLanguage);
        var results = new string[texts.Count];

        for (int i = 0; i < texts.Count; i++)
        {
            ct.ThrowIfCancellationRequested();

            if (i > 0 && delayBetweenRequestsMs > 0)
                await Task.Delay(delayBetweenRequestsMs, ct);

            var url = "https://translate.googleapis.com/translate_a/single" +
                      $"?client=gtx&sl=auto&tl={Uri.EscapeDataString(langCode)}&dt=t&q={Uri.EscapeDataString(texts[i])}";

            using var resp = await HttpClient.GetAsync(url, ct);
            var json = await resp.Content.ReadAsStringAsync(ct);

            if (!resp.IsSuccessStatusCode)
                throw new HttpRequestException($"Google Translate error {resp.StatusCode}: {json}");

            var translated = ExtractGoogleTranslateText(json);

            results[i] = translated;
            onEntryTranslated?.Invoke(i, translated);
            progress?.Report((i + 1) * 100 / texts.Count);
        }

        return [.. results];
    }

    private static string ExtractGoogleTranslateText(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0
            || root[0].ValueKind != JsonValueKind.Array)
            return "";

        var sb = new StringBuilder();
        foreach (var segment in root[0].EnumerateArray())
        {
            if (segment.ValueKind == JsonValueKind.Array && segment.GetArrayLength() > 0)
                sb.Append(segment[0].GetString());
        }

        return sb.ToString();
    }

    // Azure AI Translator language code mapping
    private static string ToAzureTranslatorLang(string language) => language.ToLowerInvariant() switch
    {
        "bulgarian" => "bg",
        "russian"   => "ru",
        "english"   => "en",
        "german"    => "de",
        "french"    => "fr",
        "spanish"   => "es",
        _           => language.ToLowerInvariant()
    };

    /// <summary>
    /// Translate a batch of texts using the Azure AI Translator REST API
    /// (works with both the global cognitive services endpoint and
    /// custom Azure AI Foundry / regional endpoints).
    /// Up to 100 elements per request; texts are batched automatically.
    /// </summary>
    public async Task<List<string>> TranslateAzureTranslatorBatchAsync(
        List<string> texts, string targetLanguage,
        string apiKey, string endpoint, string region,
        IProgress<int>? progress = null, Action<int, string>? onEntryTranslated = null,
        CancellationToken ct = default, int delayBetweenRequestsMs = 0)
    {
        const int MaxPerRequest = 10;

        var langCode  = ToAzureTranslatorLang(targetLanguage);
        var baseUri   = endpoint.TrimEnd('/');
        // Support both global endpoint and custom Foundry endpoints:
        // Global:  https://api.cognitive.microsofttranslator.com/translate?api-version=3.0
        // Foundry: https://<name>.cognitiveservices.azure.com/translator/text/v3.0/translate?api-version=3.0
        var translatePath = baseUri.Contains("cognitiveservices.azure.com")
            ? $"{baseUri}/translator/text/v3.0/translate?api-version=3.0&to={langCode}"
            : $"{baseUri}/translate?api-version=3.0&to={langCode}";

        var results = new string[texts.Count];
        int done = 0;
        int chunkIndex = 0;

        foreach (var chunk in texts.Select((t, i) => new { Text = t, Index = i }).Chunk(MaxPerRequest))
        {
            ct.ThrowIfCancellationRequested();

            if (chunkIndex > 0 && delayBetweenRequestsMs > 0)
                await Task.Delay(delayBetweenRequestsMs, ct);
            chunkIndex++;

            var body = JsonSerializer.Serialize(chunk.Select(x => new { Text = x.Text }).ToArray());

            using var req = new HttpRequestMessage(HttpMethod.Post, translatePath)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
            req.Headers.Add("Ocp-Apim-Subscription-Key", apiKey);
            if (!string.IsNullOrWhiteSpace(region))
                req.Headers.Add("Ocp-Apim-Subscription-Region", region);

            using var resp = await HttpClient.SendAsync(req, ct);
            var json = await resp.Content.ReadAsStringAsync(ct);

            if (!resp.IsSuccessStatusCode)
                throw new HttpRequestException($"Azure Translator error {resp.StatusCode}: {json}");

            using var doc = JsonDocument.Parse(json);
            var items = doc.RootElement.EnumerateArray().ToArray();

            for (int j = 0; j < chunk.Length; j++)
            {
                var translated = items[j]
                    .GetProperty("translations")[0]
                    .GetProperty("text")
                    .GetString() ?? "";

                var originalIdx = chunk[j].Index;
                results[originalIdx] = translated;
                done++;
                onEntryTranslated?.Invoke(originalIdx, translated);
                progress?.Report(done * 100 / texts.Count);
            }
        }

        return [.. results];
    }

    // ── GitHub Copilot OAuth device flow ────────────────────────────────────
    // As of 2026, api.github.com/copilot_internal/v2/token (the exchange every call to
    // api.githubcopilot.com requires) rejects personal access tokens outright — classic and
    // fine-grained alike — with "Personal Access Tokens are not supported for this endpoint".
    // The only credential that still works is a user access token obtained through GitHub's
    // OAuth device flow, the same mechanism editor integrations (VS Code, Neovim, JetBrains)
    // use. The client id below is GitHub's own public "Copilot Editor" OAuth app id — it is not
    // a secret, it's baked into every open-source Copilot client, and it only ever grants access
    // to the signed-in user's own Copilot subscription via their normal GitHub login/consent.
    private const string GitHubCopilotOAuthClientId = "Iv1.b507a08c87ecfe98";

    public record GitHubDeviceCode(string DeviceCode, string UserCode, string VerificationUri, int ExpiresIn, int Interval);

    /// <summary>Step 1 of the device flow: ask GitHub for a user code to display.</summary>
    public async Task<GitHubDeviceCode> StartGitHubDeviceFlowAsync(CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://github.com/login/device/code");
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        req.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = GitHubCopilotOAuthClientId,
            ["scope"] = "read:user"
        });
        using var resp = await HttpClient.SendAsync(req, ct);
        var json = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"Could not start GitHub sign-in ({(int)resp.StatusCode} {resp.StatusCode}): {json}");

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        return new GitHubDeviceCode(
            root.GetProperty("device_code").GetString()!,
            root.GetProperty("user_code").GetString()!,
            root.GetProperty("verification_uri").GetString()!,
            root.GetProperty("expires_in").GetInt32(),
            root.TryGetProperty("interval", out var iv) ? iv.GetInt32() : 5);
    }

    /// <summary>Step 2: poll until the user finishes authorizing in the browser, returning the long-lived access token.</summary>
    public async Task<string> PollGitHubDeviceFlowAsync(GitHubDeviceCode device, CancellationToken ct = default)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(device.Interval, 5));
        var deadline = DateTime.UtcNow.AddSeconds(device.ExpiresIn);

        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(interval, ct);

            using var req = new HttpRequestMessage(HttpMethod.Post, "https://github.com/login/oauth/access_token");
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            req.Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = GitHubCopilotOAuthClientId,
                ["device_code"] = device.DeviceCode,
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code"
            });
            using var resp = await HttpClient.SendAsync(req, ct);
            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("access_token", out var tokenEl))
                return tokenEl.GetString()!;

            var error = root.TryGetProperty("error", out var errEl) ? errEl.GetString() : null;
            switch (error)
            {
                case "authorization_pending":
                    continue;
                case "slow_down":
                    interval += TimeSpan.FromSeconds(5);
                    continue;
                case "expired_token":
                    throw new TimeoutException("The GitHub sign-in code expired before it was authorized. Please try again.");
                case "access_denied":
                    throw new InvalidOperationException("GitHub sign-in was cancelled.");
                default:
                    throw new HttpRequestException($"GitHub sign-in failed: {json}");
            }
        }

        throw new TimeoutException("The GitHub sign-in code expired before it was authorized. Please try again.");
    }

    private readonly record struct CopilotSessionToken(string Token, DateTimeOffset ExpiresAt);
    private readonly Dictionary<string, CopilotSessionToken> _copilotSessionTokens = new();
    private readonly SemaphoreSlim _copilotTokenLock = new(1, 1);

    /// <summary>
    /// Exchanges the long-lived GitHub access token (from the device flow) for the short-lived
    /// (~25-30 min) session token api.githubcopilot.com actually accepts as a Bearer token,
    /// caching it in memory until shortly before it expires.
    /// </summary>
    private async Task<string> GetCopilotSessionTokenAsync(string oauthToken, CancellationToken ct)
    {
        await _copilotTokenLock.WaitAsync(ct);
        try
        {
            if (_copilotSessionTokens.TryGetValue(oauthToken, out var cached)
                && cached.ExpiresAt > DateTimeOffset.UtcNow.AddSeconds(60))
            {
                return cached.Token;
            }

            using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/copilot_internal/v2/token");
            req.Headers.Authorization = new AuthenticationHeaderValue("token", oauthToken);
            req.Headers.UserAgent.ParseAdd("GithubCopilot/1.270.0");
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var resp = await HttpClient.SendAsync(req, ct);
            var json = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
                throw new HttpRequestException(
                    $"Could not obtain a GitHub Copilot session token ({(int)resp.StatusCode} {resp.StatusCode}): {json}. "
                    + "Sign in with GitHub again in Settings.");

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var token = root.GetProperty("token").GetString()!;
            var expiresAt = DateTimeOffset.FromUnixTimeSeconds(root.GetProperty("expires_at").GetInt64());

            var session = new CopilotSessionToken(token, expiresAt);
            _copilotSessionTokens[oauthToken] = session;
            return session.Token;
        }
        finally
        {
            _copilotTokenLock.Release();
        }
    }

    private static void ApplyCopilotHeaders(HttpRequestMessage request)
    {
        request.Headers.TryAddWithoutValidation("Copilot-Integration-Id", "vscode-chat");
        request.Headers.TryAddWithoutValidation("Editor-Version", "vscode/1.104.0");
        request.Headers.TryAddWithoutValidation("Editor-Plugin-Version", "copilot-chat/0.23.0");
        request.Headers.UserAgent.ParseAdd("GithubCopilot/1.270.0");
    }

    /// <summary>
    /// Fetches available models from the GitHub Copilot API (api.githubcopilot.com/models).
    /// GitHub Models (models.github.ai / models.inference.ai.azure.com — catalog, playground,
    /// inference API and BYOK) was fully retired by GitHub on 2026-07-30, so that endpoint is
    /// gone and must not be queried anymore.
    /// </summary>
    /// <param name="oauthToken">The long-lived GitHub access token obtained via the device flow (see <see cref="StartGitHubDeviceFlowAsync"/>).</param>
    public async Task<List<string>> FetchGitHubModelsAsync(string oauthToken, CancellationToken ct = default)
    {
        var models = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sessionToken = await GetCopilotSessionTokenAsync(oauthToken, ct);

        using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.githubcopilot.com/models");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", sessionToken);
        ApplyCopilotHeaders(req);
        using var resp = await HttpClient.SendAsync(req, ct);
        var json = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"GitHub Copilot API error ({(int)resp.StatusCode} {resp.StatusCode}): {json}");
        ExtractModelsFromArray(json, models);

        return models.OrderBy(m => m).ToList();
    }

    private static void ExtractModelsFromArray(string json, HashSet<string> target)
    {
        using var doc = JsonDocument.Parse(json);
        // Response may be a top-level array OR { "data": [...] }
        JsonElement arr = doc.RootElement.ValueKind == JsonValueKind.Array
            ? doc.RootElement
            : doc.RootElement.TryGetProperty("data", out var d) ? d : default;

        if (arr.ValueKind != JsonValueKind.Array) return;

        foreach (var item in arr.EnumerateArray())
        {
            var id = item.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";
            var name = item.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";

            string modelName;
            if (!string.IsNullOrEmpty(id) && !id.Contains("://"))
            {
                // Copilot endpoint: id may include a provider prefix (e.g. "openai/gpt-5" → "gpt-5")
                var slash = id.LastIndexOf('/');
                modelName = slash >= 0 ? id[(slash + 1)..] : id;
            }
            else if (!string.IsNullOrEmpty(id) && id.Contains("://"))
            {
                // Azure models endpoint: id is a full URI — extract name from /models/<name>/
                var match = Regex.Match(id, @"/models/([^/]+)(/|$)");
                modelName = match.Success ? match.Groups[1].Value : name;
            }
            else
            {
                modelName = name;
            }

            if (!string.IsNullOrEmpty(modelName))
                target.Add(modelName);
        }
    }

    /// <summary>
    /// Fetches available models from the Groq API (OpenAI-compatible /v1/models endpoint).
    /// </summary>
    public async Task<List<string>> FetchGroqModelsAsync(string apiKey, CancellationToken ct = default)
    {
        var models = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        using var req = new HttpRequestMessage(HttpMethod.Get,
            "https://api.x.ai/v1/models");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        using var resp = await HttpClient.SendAsync(req, ct);
        var json = await resp.Content.ReadAsStringAsync(ct);
        if (resp.IsSuccessStatusCode)
            ExtractModelsFromArray(json, models);

        return models.OrderBy(m => m).ToList();
    }

    /// <summary>
    /// Fetches currently available free models from OpenRouter API.
    /// </summary>
    public async Task<List<string>> FetchOpenRouterFreeModelsAsync(string apiKey, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://openrouter.ai/api/v1/models");
        if (!string.IsNullOrWhiteSpace(apiKey))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        using var response = await HttpClient.SendAsync(request, ct);
        var json = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"OpenRouter API error: {response.StatusCode}");

        using var doc = JsonDocument.Parse(json);
        var models = new List<string>();

        foreach (var item in doc.RootElement.GetProperty("data").EnumerateArray())
        {
            var id = item.GetProperty("id").GetString() ?? "";
            if (!item.TryGetProperty("pricing", out var pricing)) continue;

            var promptPrice = pricing.TryGetProperty("prompt", out var p) ? p.GetString() : null;
            var completionPrice = pricing.TryGetProperty("completion", out var c) ? c.GetString() : null;

            if (promptPrice == "0" && completionPrice == "0" && !string.IsNullOrEmpty(id))
            {
                models.Add(id);
            }
        }

        return models.OrderBy(m => m).ToList();
    }

    /// <summary>
    /// Fetches available local models from a llama.cpp / LM Studio / Ollama-compatible server.
    /// Tries OpenAI-compatible model endpoints first, then falls back to llama.cpp /props.
    /// </summary>
    public async Task<List<string>> FetchOllamaModelsAsync(string endpoint, CancellationToken ct = default)
    {
        var root = NormalizeModelEndpointRoot(endpoint);
        if (string.IsNullOrWhiteSpace(root))
            throw new ArgumentException("Endpoint cannot be empty.", nameof(endpoint));

        var models = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var fetchedAny = false;

        if (await TryFetchOpenAiModelsAsync(new Uri(new Uri(root), "v1/models"), models, ct))
            fetchedAny = true;

        if (await TryFetchOpenAiModelsAsync(new Uri(new Uri(root), "models"), models, ct))
            fetchedAny = true;

        if (await TryFetchLlamaCppPropsAsync(new Uri(new Uri(root), "props"), models, ct))
            fetchedAny = true;

        if (await TryFetchOllamaTagsAsync(new Uri(new Uri(root), "api/tags"), models, ct))
            fetchedAny = true;

        if (!fetchedAny)
            throw new HttpRequestException("Unable to fetch models from llama.cpp, LM Studio, or Ollama.");

        return models.OrderBy(m => m).ToList();
    }

    private static string NormalizeModelEndpointRoot(string endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
            return "";

        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri))
            return endpoint.TrimEnd('/') + "/";

        var path = uri.AbsolutePath;
        foreach (var suffix in new[] { "/v1/chat/completions", "/chat/completions", "/completions" })
        {
            if (path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                path = path[..^suffix.Length];
                break;
            }
        }

        if (!path.EndsWith('/'))
            path += "/";

        return new UriBuilder(uri) { Path = path }.Uri.ToString();
    }

    private static async Task<bool> TryFetchOllamaTagsAsync(Uri requestUri, HashSet<string> target, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        using var response = await HttpClient.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            return false;

        var json = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);

        if (!doc.RootElement.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Array)
            return true;

        foreach (var item in models.EnumerateArray())
        {
            var name = item.TryGetProperty("name", out var nameEl) ? nameEl.GetString() : null;
            var model = item.TryGetProperty("model", out var modelEl) ? modelEl.GetString() : null;
            var id = item.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;

            var modelName = name ?? model ?? id ?? "";
            if (!string.IsNullOrWhiteSpace(modelName))
                target.Add(modelName);
        }

        return true;
    }

    private static async Task<bool> TryFetchOpenAiModelsAsync(Uri requestUri, HashSet<string> target, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        using var response = await HttpClient.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            return false;

        var json = await response.Content.ReadAsStringAsync(ct);
        ExtractModelsFromArray(json, target);
        return true;
    }

    private static async Task<bool> TryFetchLlamaCppPropsAsync(Uri requestUri, HashSet<string> target, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        using var response = await HttpClient.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            return false;

        var json = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        AddIfPresent(root, "model", target);
        AddIfPresent(root, "model_alias", target);

        if (root.TryGetProperty("default_generation_settings", out var settings)
            && settings.ValueKind == JsonValueKind.Object)
        {
            AddIfPresent(settings, "model", target);
            AddIfPresent(settings, "model_alias", target);
        }

        if (root.TryGetProperty("model_path", out var modelPathEl))
        {
            var modelPath = modelPathEl.GetString();
            if (!string.IsNullOrWhiteSpace(modelPath))
            {
                var fileName = Path.GetFileNameWithoutExtension(modelPath);
                if (!string.IsNullOrWhiteSpace(fileName))
                    target.Add(fileName);
            }
        }

        return true;
    }

    private static void AddIfPresent(JsonElement element, string propertyName, HashSet<string> target)
    {
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.String)
            return;

        var value = property.GetString();
        if (!string.IsNullOrWhiteSpace(value))
            target.Add(value);
    }

    public async Task<string> TranslateTextAsync(
        string text, string targetLanguage, string apiKey, string model, string endpoint,
        AppSettings? settings = null, CancellationToken ct = default)
    {
        var actualModel = settings != null ? GetNextModel(settings) : model;

        if (IsLocalTranslationProvider(settings, endpoint))
            return await TranslateLocalTextAsync(text, targetLanguage, apiKey, actualModel, endpoint, settings, ct);

        var systemPrompt = $"You are a professional translator. Translate the following text to {targetLanguage}. " +
                           "Preserve all formatting, markdown syntax, line breaks, and special characters exactly as they are. " +
                           "Only translate the text content. Do not add explanations.";

        return await CallApiWithRetryAsync(systemPrompt, text, apiKey, actualModel, endpoint,
            settings, ct);
    }

    /// <summary>
    /// Sends a custom system prompt and user message using the active translation provider settings.
    /// </summary>
    public async Task<string> CallAiAsync(
        string systemPrompt, string userMessage,
        AppSettings settings, CancellationToken ct = default)
    {
        var model    = GetNextModel(settings);
        var apiKey   = settings.ActiveApiKey;
        var endpoint = settings.ActiveEndpoint;
        return await CallApiWithRetryAsync(systemPrompt, userMessage, apiKey, model, endpoint, settings, ct);
    }

    /// <summary>
    /// Sends a custom system prompt and user message using the active CHAT provider settings
    /// (same provider used by the Preview / AI Assistant tab).
    /// </summary>
    public async Task<string> CallChatAiAsync(
        string systemPrompt, string userMessage,
        AppSettings settings, CancellationToken ct = default)
    {
        var apiKey   = settings.ChatActiveApiKey;
        var model    = settings.ChatActiveModel;
        var endpoint = settings.ChatActiveEndpoint;
        return await CallApiWithRetryAsync(systemPrompt, userMessage, apiKey, model, endpoint, settings, ct);
    }

    public async Task<List<string>> TranslateSubtitleBatchAsync(
        List<string> texts, string targetLanguage, string apiKey, string model, string endpoint,
        int batchSize = 30, int delayBetweenRequestsMs = 0,
        IProgress<int>? progress = null, Action<int, string>? onEntryTranslated = null,
        AppSettings? settings = null, CancellationToken ct = default)
    {
        if (IsLocalTranslationProvider(settings, endpoint))
        {
            var individualResults = new string[texts.Count];

            for (int i = 0; i < texts.Count; i++)
            {
                ct.ThrowIfCancellationRequested();

                if (i > 0 && delayBetweenRequestsMs > 0)
                    await Task.Delay(delayBetweenRequestsMs, ct);

                var translated = await TranslateLocalTextAsync(
                    texts[i], targetLanguage, apiKey, model, endpoint, settings, ct);

                individualResults[i] = translated;
                onEntryTranslated?.Invoke(i, translated);
                progress?.Report((i + 1) * 100 / texts.Count);
            }

            return [.. individualResults];
        }

        var results = new string[texts.Count];
        var batches = texts.Select((t, i) => new { Text = t, Index = i })
            .Chunk(batchSize)
            .ToList();

        int completed = 0;

        for (int b = 0; b < batches.Count; b++)
        {
            ct.ThrowIfCancellationRequested();

            // Rate limiting: pause between requests (skip before first)
            if (b > 0 && delayBetweenRequestsMs > 0)
            {
                await Task.Delay(delayBetweenRequestsMs, ct);
            }

            var actualModel = settings != null ? GetNextModel(settings) : model;

            var batch = batches[b];
            // Encode multi-line subtitles: replace newlines with " | " so each entry stays on one numbered line
            var numberedInput = string.Join("\n", batch.Select(item =>
                $"[{item.Index}] {item.Text.Replace("\r\n", " | ").Replace("\n", " | ")}"));

            var systemPrompt = $"You are a professional subtitle translator. Translate each numbered line to {targetLanguage}. " +
                               "Keep the [number] prefix for each line. Preserve line numbering exactly. " +
                               "The ' | ' separator represents line breaks in the subtitle — keep them as ' | ' in the translation. " +
                               "Only output the translated lines, nothing else.";

            var response = await CallApiWithRetryAsync(systemPrompt, numberedInput, apiKey, actualModel, endpoint,
                settings, ct);

            foreach (var line in response.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var match = Regex.Match(line.Trim(), @"^\[(\d+)\]\s*(.+)$");
                if (match.Success && int.TryParse(match.Groups[1].Value, out var idx) && idx >= 0 && idx < texts.Count)
                {
                    // Decode " | " back to newlines
                    var translated = match.Groups[2].Value.Trim().Replace(" | ", "\n");
                    results[idx] = translated;
                    onEntryTranslated?.Invoke(idx, translated);
                }
            }

            completed += batch.Length;
            progress?.Report((int)((double)completed / texts.Count * 100));
        }

        return results.Select(r => r ?? "").ToList();
    }

    private async Task<string> CallApiWithRetryAsync(
        string systemPrompt, string userMessage, string apiKey, string model, string endpoint,
        AppSettings? settings = null, CancellationToken ct = default)
    {
        for (int attempt = 0; attempt <= MaxRetries; attempt++)
        {
            try
            {
                return await CallApiAsync(systemPrompt, userMessage, apiKey, model, endpoint, ct, settings);
            }
            catch (HttpRequestException ex) when (attempt < MaxRetries && (ex.Message.Contains("429") || ex.Message.Contains("404") || ex.Message.Contains("unknown_model")))
            {
                Debug.WriteLine($"[TranslationService] Attempt {attempt + 1}/{MaxRetries} failed: {ex.Message}");
                // On rate-limit or unknown model, try switching to a different model if auto-rotate is on
                if (settings is { Provider: AiProvider.OpenRouter, OpenRouterAutoRotate: true }
                    && settings.OpenRouterFreeModels.Count > 1)
                {
                    model = GetNextModel(settings);
                    Debug.WriteLine($"[TranslationService] Switching to model: {model}");
                    await Task.Delay(2000, ct);
                }
                else if (ex.Message.Contains("unknown_model"))
                {
                    // Unknown model — no point retrying with the same model, surface immediately
                    throw;
                }
                else
                {
                    // Exponential backoff: 5s, 15s, 45s
                    var delaySec = 5 * (int)Math.Pow(3, attempt);
                    Debug.WriteLine($"[TranslationService] Retrying in {delaySec}s...");
                    await Task.Delay(delaySec * 1000, ct);
                }
            }
        }

        return await CallApiAsync(systemPrompt, userMessage, apiKey, model, endpoint, ct, settings);
    }

    private async Task<string> CallApiAsync(
        string systemPrompt, string userMessage, string apiKey, string model, string endpoint,
        CancellationToken ct, AppSettings? settings = null, int? maxTokensOverride = null)
    {
        if (settings?.Provider == AiProvider.Gemini)
            return await CallGeminiApiAsync(systemPrompt, userMessage, apiKey, model, endpoint, ct, settings);

        var messages = IsLocalTranslationProvider(settings, endpoint) && string.IsNullOrWhiteSpace(systemPrompt)
            ? new object[]
            {
                new { role = "user", content = userMessage }
            }
            : new object[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userMessage }
            };

        var isLocalTranslation = IsLocalTranslationProvider(settings, endpoint) && string.IsNullOrWhiteSpace(systemPrompt);
        var maxTokens = maxTokensOverride ?? (isLocalTranslation ? EstimateLocalTranslationMaxTokens(userMessage) : (int?)null);

        var requestBody = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["messages"] = messages,
            ["temperature"] = isLocalTranslation ? GetLocalTranslationTemperature() : settings?.Temperature ?? 1.0
        };

        if (maxTokens.HasValue)
            requestBody["max_tokens"] = maxTokens;

        var json = JsonSerializer.Serialize(requestBody);
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        var isCopilotEndpoint = endpoint.Contains("api.githubcopilot.com", StringComparison.OrdinalIgnoreCase);
        if (isCopilotEndpoint && !string.IsNullOrWhiteSpace(apiKey))
        {
            // apiKey here is the long-lived device-flow access token; exchange it for the
            // short-lived session token the Copilot API actually accepts as a Bearer token.
            var sessionToken = await GetCopilotSessionTokenAsync(apiKey, ct);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", sessionToken);
            ApplyCopilotHeaders(request);
        }
        // Local OpenAI-compatible servers can work without an API key; skip the header when key is empty
        else if (!string.IsNullOrWhiteSpace(apiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");

        using var response = await HttpClient.SendAsync(request, ct);
        var responseJson = await response.Content.ReadAsStringAsync(ct);

        Debug.WriteLine($"[TranslationService] {(int)response.StatusCode} {response.StatusCode} — {responseJson}");

        if (!response.IsSuccessStatusCode)
        {
            // Include status code number for retry detection
            throw new HttpRequestException(
                $"API error ({(int)response.StatusCode} {response.StatusCode}): {responseJson}");
        }

        using var doc = JsonDocument.Parse(responseJson);
        var (content, truncatedWhileReasoning) = ParseOpenAiChoice(doc.RootElement);

        if (truncatedWhileReasoning && maxTokensOverride == null)
        {
            Debug.WriteLine("[TranslationService] Reasoning model exhausted its budget before answering — retrying with a larger token budget.");
            var boosted = maxTokens.HasValue
                ? Math.Min(maxTokens.Value * 4, ReasoningRetryMaxTokens)
                : ReasoningRetryDefaultTokens;
            return await CallApiAsync(systemPrompt, userMessage, apiKey, model, endpoint, ct, settings, boosted);
        }

        return content;
    }

    // ── Reasoning ("thinking") model output cleanup ─────────────────────────
    // Reasoning models (DeepSeek R1, QwQ, some GitHub Copilot models, etc.) emit their
    // chain-of-thought inline in the response — typically wrapped in <think>/<thinking>/
    // <reasoning> tags — ahead of the actual answer. Since callers (translation parsing,
    // chat display) only expect the final answer, strip that block out before returning.
    private static readonly Regex ThinkingBlockRegex = new(
        @"<(think|thinking|reasoning)>.*?</\1>",
        RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static string StripThinkingBlock(string content)
    {
        if (string.IsNullOrEmpty(content))
            return content;

        var stripped = ThinkingBlockRegex.Replace(content, "");

        // Truncated output (hit the token limit mid-thought) can leave an unclosed opening
        // tag with no matching close — there's no real answer left after it, so drop the rest.
        var openTagMatch = Regex.Match(stripped, @"<(think|thinking|reasoning)>", RegexOptions.IgnoreCase);
        if (openTagMatch.Success)
            stripped = stripped[..openTagMatch.Index];

        return stripped.Trim();
    }

    /// <summary>
    /// Extracts the assistant's content from an OpenAI-compatible <c>choices[0].message</c> object,
    /// stripping any inline thinking block, and flags the case where a reasoning model burned its
    /// whole token budget thinking (finish_reason=length) and left content empty with the chain-of-
    /// thought only in a separate reasoning_content field — a signal that a bigger budget is needed.
    /// </summary>
    private static (string Content, bool TruncatedWhileReasoning) ParseOpenAiChoice(JsonElement root)
    {
        var choice = root.GetProperty("choices")[0];
        var message = choice.GetProperty("message");

        var rawContent = message.TryGetProperty("content", out var contentEl) && contentEl.ValueKind == JsonValueKind.String
            ? contentEl.GetString() ?? ""
            : "";
        var content = StripThinkingBlock(rawContent);

        var finishReason = choice.TryGetProperty("finish_reason", out var frEl) ? frEl.GetString() : null;
        var hasReasoning = message.TryGetProperty("reasoning_content", out var reasoningEl)
            && reasoningEl.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(reasoningEl.GetString());

        var truncatedWhileReasoning = string.IsNullOrWhiteSpace(content) && finishReason == "length" && hasReasoning;
        return (content, truncatedWhileReasoning);
    }

    private async Task<string> CallGeminiApiAsync(
        string systemPrompt, string userMessage, string apiKey, string model, string baseEndpoint,
        CancellationToken ct, AppSettings? settings = null)
    {
        // Gemini uses key in query string and model in path
        var url = $"{baseEndpoint}/{model}:generateContent?key={Uri.EscapeDataString(apiKey)}";

        var requestBody = new
        {
            system_instruction = new
            {
                parts = new[] { new { text = systemPrompt } }
            },
            contents = new[]
            {
                new { role = "user", parts = new[] { new { text = userMessage } } }
            },
            generationConfig = new { temperature = settings?.Temperature ?? 0.3 }
        };

        var json = JsonSerializer.Serialize(requestBody);
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");

        using var response = await HttpClient.SendAsync(request, ct);
        var responseJson = await response.Content.ReadAsStringAsync(ct);

        Debug.WriteLine($"[TranslationService][Gemini] {(int)response.StatusCode} {response.StatusCode} — {responseJson}");

        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"API error ({(int)response.StatusCode} {response.StatusCode}): {responseJson}");

        using var doc = JsonDocument.Parse(responseJson);
        return StripThinkingBlock(doc.RootElement
            .GetProperty("candidates")[0]
            .GetProperty("content")
            .GetProperty("parts")[0]
            .GetProperty("text")
            .GetString() ?? "");
    }

    // ──────────────────────────────────────────────────────────────────────────
    //  Multi-turn chat with history
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Sends a new user message together with the full conversation history
    /// and returns the assistant's reply.
    /// </summary>
    public Task<string> ChatWithHistoryAsync(
        string systemPrompt,
        IReadOnlyList<ChatMessage> history,
        string userMessage,
        AppSettings? settings = null,
        CancellationToken ct = default)
    {
        var provider = settings?.EffectiveChatProvider ?? AiProvider.OpenAI;
        return ChatWithHistoryAsync(
            systemPrompt, history, userMessage, provider,
            settings?.ChatActiveApiKey ?? "", settings?.ChatActiveModel ?? "", settings?.ChatActiveEndpoint ?? "",
            settings?.Temperature ?? 1.0, ct);
    }

    /// <summary>
    /// Sends a new user message together with the full conversation history using an explicit
    /// provider/model/endpoint rather than the single app-wide "Chat AI" setting — used by the
    /// standalone AI Chat tab, where the provider and model are chosen per-session.
    /// </summary>
    public Task<string> ChatWithHistoryAsync(
        string systemPrompt,
        IReadOnlyList<ChatMessage> history,
        string userMessage,
        AiProvider provider,
        string apiKey,
        string model,
        string endpoint,
        double temperature,
        CancellationToken ct = default)
        => ChatWithHistoryAsync(systemPrompt, history, userMessage, provider, apiKey, model, endpoint, temperature, null, ct);

    private async Task<string> ChatWithHistoryAsync(
        string systemPrompt,
        IReadOnlyList<ChatMessage> history,
        string userMessage,
        AiProvider provider,
        string apiKey,
        string model,
        string endpoint,
        double temperature,
        int? maxTokensOverride,
        CancellationToken ct)
    {
        if (provider == AiProvider.Gemini)
            return await CallGeminiChatWithHistoryAsync(systemPrompt, history, userMessage, apiKey, model, endpoint, temperature, ct);

        // Build OpenAI-compatible messages array
        var messages = new List<object>
        {
            new { role = "system", content = systemPrompt }
        };

        foreach (var msg in history)
        {
            messages.Add(new
            {
                role = msg.Role == ChatRole.User ? "user" : "assistant",
                content = msg.Content
            });
        }

        messages.Add(new { role = "user", content = userMessage });

        var requestBody = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["messages"] = messages,
            ["temperature"] = temperature
        };

        if (maxTokensOverride.HasValue)
            requestBody["max_tokens"] = maxTokensOverride;

        var json = JsonSerializer.Serialize(requestBody);
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        var isCopilotEndpoint = endpoint.Contains("api.githubcopilot.com", StringComparison.OrdinalIgnoreCase);
        if (isCopilotEndpoint && !string.IsNullOrWhiteSpace(apiKey))
        {
            // apiKey here is the long-lived device-flow access token; exchange it for the
            // short-lived session token the Copilot API actually accepts as a Bearer token.
            var sessionToken = await GetCopilotSessionTokenAsync(apiKey, ct);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", sessionToken);
            ApplyCopilotHeaders(request);
        }
        // Local OpenAI-compatible servers can work without an API key; skip the header when key is empty
        else if (!string.IsNullOrWhiteSpace(apiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");

        using var response = await HttpClient.SendAsync(request, ct);
        var responseJson = await response.Content.ReadAsStringAsync(ct);

        Debug.WriteLine($"[TranslationService][Chat] {(int)response.StatusCode} {response.StatusCode} — {responseJson}");

        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"API error ({(int)response.StatusCode} {response.StatusCode}): {responseJson}");

        using var doc = JsonDocument.Parse(responseJson);
        var (content, truncatedWhileReasoning) = ParseOpenAiChoice(doc.RootElement);

        if (truncatedWhileReasoning && maxTokensOverride == null)
        {
            Debug.WriteLine("[TranslationService][Chat] Reasoning model exhausted its budget before answering — retrying with a larger token budget.");
            return await ChatWithHistoryAsync(
                systemPrompt, history, userMessage, provider, apiKey, model, endpoint, temperature,
                ReasoningRetryDefaultTokens, ct);
        }

        return content;
    }

    private async Task<string> CallGeminiChatWithHistoryAsync(
        string systemPrompt,
        IReadOnlyList<ChatMessage> history,
        string userMessage,
        string apiKey,
        string model,
        string baseEndpoint,
        double temperature,
        CancellationToken ct)
    {
        var endpoint = baseEndpoint.TrimEnd('/');
        var url = $"{endpoint}/{model}:generateContent?key={Uri.EscapeDataString(apiKey)}";

        // Gemini requires alternating user/model turns; merge consecutive same-role messages
        var contents = new List<object>();
        foreach (var msg in history)
        {
            var geminiRole = msg.Role == ChatRole.User ? "user" : "model";
            contents.Add(new { role = geminiRole, parts = new[] { new { text = msg.Content } } });
        }
        contents.Add(new { role = "user", parts = new[] { new { text = userMessage } } });

        var requestBody = new
        {
            system_instruction = new { parts = new[] { new { text = systemPrompt } } },
            contents,
            generationConfig = new { temperature }
        };

        var json = JsonSerializer.Serialize(requestBody);
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");

        using var response = await HttpClient.SendAsync(request, ct);
        var responseJson = await response.Content.ReadAsStringAsync(ct);

        Debug.WriteLine($"[TranslationService][GeminiChat] {(int)response.StatusCode} — {responseJson}");

        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"API error ({(int)response.StatusCode} {response.StatusCode}): {responseJson}");

        using var doc = JsonDocument.Parse(responseJson);
        return StripThinkingBlock(doc.RootElement
            .GetProperty("candidates")[0]
            .GetProperty("content")
            .GetProperty("parts")[0]
            .GetProperty("text")
            .GetString() ?? "");
    }
}
