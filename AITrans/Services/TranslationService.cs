using System;
using System.Collections.Generic;
using System.Diagnostics;
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

    public async Task<string> TranslateTextAsync(
        string text, string targetLanguage, string apiKey, string model, string endpoint,
        AppSettings? settings = null, CancellationToken ct = default)
    {
        var actualModel = settings != null ? GetNextModel(settings) : model;

        var systemPrompt = $"You are a professional translator. Translate the following text to {targetLanguage}. " +
                           "Preserve all formatting, markdown syntax, line breaks, and special characters exactly as they are. " +
                           "Only translate the text content. Do not add explanations.";

        return await CallApiWithRetryAsync(systemPrompt, text, apiKey, actualModel, endpoint,
            settings, ct);
    }

    public async Task<List<string>> TranslateSubtitleBatchAsync(
        List<string> texts, string targetLanguage, string apiKey, string model, string endpoint,
        int batchSize = 30, int delayBetweenRequestsMs = 0,
        IProgress<int>? progress = null, Action<int, string>? onEntryTranslated = null,
        AppSettings? settings = null, CancellationToken ct = default)
    {
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

        var requestBody = new
        {
            model,
            messages = new object[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userMessage }
            },
            temperature = settings?.Temperature ?? 1.0
        };

        var json = JsonSerializer.Serialize(requestBody);
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
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
