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
    private const int LocalTranslationMinTokens = 256;
    private const int LocalTranslationMaxTokens = 2048;
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

    // ──────────────────────────────────────────────────────────────────────────
    //  DeepL (free, unofficial jsonrpc endpoint — no API key required)
    //  Reverse-engineered from deepl.com/translator's own web client, same technique
    //  used by community tools like DeepLX. Unlike the Google free endpoint, DeepL
    //  actively fights unofficial clients, so this is inherently more fragile and can
    //  break whenever they change their bot-detection heuristics.
    // ──────────────────────────────────────────────────────────────────────────

    private long _deepLFreeRequestId = Random.Shared.NextInt64(8_300_000, 8_399_998) * 1000;

    /// <summary>
    /// Translate a batch of texts using the free, unofficial DeepL jsonrpc endpoint
    /// (www2.deepl.com/jsonrpc). No API key required, but this endpoint is undocumented,
    /// rate-limited, and may change or block requests without notice.
    /// </summary>
    public async Task<List<string>> TranslateDeepLFreeBatchAsync(
        List<string> texts, string targetLanguage,
        IProgress<int>? progress = null, Action<int, string>? onEntryTranslated = null,
        CancellationToken ct = default, int delayBetweenRequestsMs = 0)
    {
        var langCode = ToDeepLLang(targetLanguage);
        var results = new string[texts.Count];

        for (int i = 0; i < texts.Count; i++)
        {
            ct.ThrowIfCancellationRequested();

            if (i > 0 && delayBetweenRequestsMs > 0)
                await Task.Delay(delayBetweenRequestsMs, ct);

            var translated = await CallDeepLFreeApiAsync(texts[i], langCode, ct);

            results[i] = translated;
            onEntryTranslated?.Invoke(i, translated);
            progress?.Report((i + 1) * 100 / texts.Count);
        }

        return [.. results];
    }

    private async Task<string> CallDeepLFreeApiAsync(string text, string targetLang, CancellationToken ct)
    {
        var iCount = text.Count(c => c == 'i');
        var id = ++_deepLFreeRequestId;
        var timestamp = GetDeepLFreeTimestamp(iCount);

        var payload = new
        {
            jsonrpc = "2.0",
            method = "LMT_handle_texts",
            id,
            @params = new
            {
                texts = new[] { new { text, requestAlternatives = 0 } },
                splitting = "newlines",
                lang = new
                {
                    source_lang_user_selected = "auto",
                    target_lang = targetLang
                },
                timestamp
            }
        };

        var json = JsonSerializer.Serialize(payload);
        // DeepL's web client fingerprints its own JSON formatting around "method" for
        // certain request ids — mirror that quirk or the request gets flagged as a bot.
        json = (id + 5) % 29 == 0 || id % 13 == 0
            ? json.Replace("\"method\":\"", "\"method\" : \"")
            : json;

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://www2.deepl.com/jsonrpc?method=LMT_handle_texts");
        request.Headers.Add("Accept", "*/*");
        request.Headers.Add("Origin", "https://www.deepl.com");
        request.Headers.Add("Referer", "https://www.deepl.com/");
        request.Headers.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");

        using var response = await HttpClient.SendAsync(request, ct);
        var responseJson = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"DeepL (free) error {response.StatusCode}: {responseJson}");

        using var doc = JsonDocument.Parse(responseJson);
        if (doc.RootElement.TryGetProperty("error", out var errorEl))
            throw new HttpRequestException($"DeepL (free) error: {errorEl}");

        return doc.RootElement
            .GetProperty("result")
            .GetProperty("texts")[0]
            .GetProperty("text")
            .GetString() ?? "";
    }

    private static long GetDeepLFreeTimestamp(long iCount)
    {
        var ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (iCount == 0) return ts;

        iCount += 1;
        return ts - (ts % iCount) + iCount;
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

    /// <summary>
    /// Fetches available models from the GitHub Models API and GitHub Copilot Pro API
    /// (models.inference.ai.azure.com + api.githubcopilot.com), merging both lists.
    /// </summary>
    public async Task<List<string>> FetchGitHubModelsAsync(string apiKey, CancellationToken ct = default)
    {
        var models = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // ── Endpoint 1: GitHub Models (free + Pro)
        try
        {
            using var req1 = new HttpRequestMessage(HttpMethod.Get,
                "https://models.github.ai/catalog/models");
               // "https://models.inference.ai.azure.com/models");
            req1.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            using var resp1 = await HttpClient.SendAsync(req1, ct);
            var json1 = await resp1.Content.ReadAsStringAsync(ct);
            if (resp1.IsSuccessStatusCode)
                ExtractModelsFromArray(json1, models);
        }
        catch { /* ignore — Copilot endpoint may still succeed */ }

        // ── Endpoint 2: Copilot Pro models
        try
        {
            using var req2 = new HttpRequestMessage(HttpMethod.Get,
                "https://api.githubcopilot.com/models");
            req2.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            using var resp2 = await HttpClient.SendAsync(req2, ct);
            var json2 = await resp2.Content.ReadAsStringAsync(ct);
            if (resp2.IsSuccessStatusCode)
                ExtractModelsFromArray(json2, models);
        }
        catch { /* ignore */ }

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
        CancellationToken ct, AppSettings? settings = null)
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
        var maxTokens = isLocalTranslation ? EstimateLocalTranslationMaxTokens(userMessage) : (int?)null;

        var requestBody = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["messages"] = messages,
            ["temperature"] = isLocalTranslation ? GetLocalTranslationTemperature() : settings?.Temperature ?? 1.0
        };

        if (isLocalTranslation)
            requestBody["max_tokens"] = maxTokens;

        var json = JsonSerializer.Serialize(requestBody);
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        // Local OpenAI-compatible servers can work without an API key; skip the header when key is empty
        if (!string.IsNullOrWhiteSpace(apiKey))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
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
        return doc.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString() ?? "";
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
        return doc.RootElement
            .GetProperty("candidates")[0]
            .GetProperty("content")
            .GetProperty("parts")[0]
            .GetProperty("text")
            .GetString() ?? "";
    }

    // ──────────────────────────────────────────────────────────────────────────
    //  Multi-turn chat with history
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Sends a new user message together with the full conversation history
    /// and returns the assistant's reply.
    /// </summary>
    public async Task<string> ChatWithHistoryAsync(
        string systemPrompt,
        IReadOnlyList<ChatMessage> history,
        string userMessage,
        AppSettings? settings = null,
        CancellationToken ct = default)
    {
        if (settings?.EffectiveChatProvider == AiProvider.Gemini)
            return await CallGeminiChatWithHistoryAsync(systemPrompt, history, userMessage, settings, ct);

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

        var apiKey   = settings?.ChatActiveApiKey   ?? "";
        var model    = settings?.ChatActiveModel ?? "";
        var endpoint = settings?.ChatActiveEndpoint ?? "";

        var requestBody = new
        {
            model,
            messages,
            temperature = settings?.Temperature ?? 1.0
        };

        var json = JsonSerializer.Serialize(requestBody);
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");

        using var response = await HttpClient.SendAsync(request, ct);
        var responseJson = await response.Content.ReadAsStringAsync(ct);

        Debug.WriteLine($"[TranslationService][Chat] {(int)response.StatusCode} {response.StatusCode} — {responseJson}");

        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"API error ({(int)response.StatusCode} {response.StatusCode}): {responseJson}");

        using var doc = JsonDocument.Parse(responseJson);
        return doc.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString() ?? "";
    }

    private async Task<string> CallGeminiChatWithHistoryAsync(
        string systemPrompt,
        IReadOnlyList<ChatMessage> history,
        string userMessage,
        AppSettings settings,
        CancellationToken ct)
    {
        var model    = settings.ChatActiveModel;
        var endpoint = settings.ChatActiveEndpoint.TrimEnd('/');
        var url      = $"{endpoint}/{model}:generateContent?key={Uri.EscapeDataString(settings.GeminiApiKey)}";

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
            generationConfig = new { temperature = settings.Temperature }
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
        return doc.RootElement
            .GetProperty("candidates")[0]
            .GetProperty("content")
            .GetProperty("parts")[0]
            .GetProperty("text")
            .GetString() ?? "";
    }
}
