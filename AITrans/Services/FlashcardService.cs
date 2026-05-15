using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AITrans.Models;

namespace AITrans.Services;

public class FlashcardService
{
    private readonly CacheService _cacheService;

    public FlashcardService(CacheService cacheService)
    {
        _cacheService = cacheService;
    }

    public Task<List<FlashCard>> GetAllCardsAsync() =>
        Task.FromResult(_cacheService.GetAllFlashCards());

    public Task<int> SaveCardAsync(FlashCard card) =>
        Task.FromResult(_cacheService.SaveFlashCard(card));

    public Task DeleteCardAsync(int id)
    {
        _cacheService.DeleteFlashCard(id);
        return Task.CompletedTask;
    }

    public Task UpdateStatsAsync(int id, bool correct)
    {
        _cacheService.UpdateFlashCardStats(id, correct);
        return Task.CompletedTask;
    }

    public Task UpdateCardAsync(FlashCard card)
    {
        _cacheService.UpdateFlashCard(card);
        return Task.CompletedTask;
    }

    public Task UpdateRatingAsync(int id, CardRating rating)
    {
        _cacheService.UpdateFlashCardRating(id, rating);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Parses a semicolon-delimited CSV file with three columns:
    /// front;back;usage
    /// Quoted fields (RFC 4180) are supported.
    /// Skips the header row if the first cell equals "front" (case-insensitive).
    /// </summary>
    public async Task<List<FlashCard>> ImportFromCsvAsync(string filePath)
    {
        var lines = await File.ReadAllLinesAsync(filePath, Encoding.UTF8);
        var result = new List<FlashCard>();
        bool firstLine = true;

        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (string.IsNullOrEmpty(line)) continue;

            var parts = SplitCsvLine(line, ';');

            // Skip header row
            if (firstLine)
            {
                firstLine = false;
                if (parts.Length > 0 &&
                    string.Equals(parts[0], "front", StringComparison.OrdinalIgnoreCase))
                    continue;
            }

            var front = parts.Length > 0 ? parts[0].Trim() : "";
            var back  = parts.Length > 1 ? parts[1].Trim() : "";
            var usage = parts.Length > 2 ? parts[2].Trim() : "";

            if (string.IsNullOrEmpty(front)) continue;

            var card = new FlashCard
            {
                FrontText = front,
                BackText  = back,
                UsageText = usage,
                CreatedAt = DateTime.UtcNow
            };

            var id = _cacheService.SaveFlashCard(card);
            card.Id = id;
            result.Add(card);
        }

        return result;
    }

    /// <summary>
    /// Uses the configured AI provider to fill in the back (translation) and
    /// usage (examples / thesaurus) sides for a given front-side word/phrase.
    /// Returns (backText, usageText).
    /// </summary>
    public async Task<(string Back, string Usage)> GenerateAiSidesAsync(
        string frontText,
        TranslationService translationService,
        AppSettings settings,
        CancellationToken ct = default)
    {
        var systemPrompt =
            "You are a language learning assistant. " +
            "Given a foreign word or phrase, respond ONLY with a JSON object (no markdown, no extra text) " +
            "in this exact format: {\"translation\": \"...\", \"usage\": \"...\"}\n" +
            "- 'translation': the top 3 most common Bulgarian meanings/translations of the word, " +
            "numbered and separated by newlines, e.g. '1. значение едно\\n2. значение две\\n3. значение три'. " +

            "- 'usage': 2-3 example sentences IN THE ORIGINAL FOREIGN LANGUAGE showing how the word is used, " +
            "plus 2-3 synonyms / thesaurus entries IN THE ORIGINAL FOREIGN LANGUAGE. " +
            "Do NOT translate the usage text to Bulgarian.";

        var response = await translationService.CallChatAiAsync(
            systemPrompt, frontText, settings, ct);

        return ParseAiResponse(response);
    }

    // ─── Helpers ──────────────────────────────────────────────────────────────

    private static (string Back, string Usage) ParseAiResponse(string json)
    {
        // Strip markdown code fences if the model wrapped the JSON
        var cleaned = json.Trim();
        if (cleaned.StartsWith("```"))
        {
            var start = cleaned.IndexOf('\n');
            var end   = cleaned.LastIndexOf("```");
            if (start >= 0 && end > start)
                cleaned = cleaned[(start + 1)..end].Trim();
        }

        try
        {
            using var doc = JsonDocument.Parse(cleaned);
            var root  = doc.RootElement;
            var back  = root.TryGetProperty("translation", out var t) ? t.GetString() ?? "" : "";
            var usage = root.TryGetProperty("usage",       out var u) ? u.GetString() ?? "" : "";
            return (back, usage);
        }
        catch
        {
            // Fallback: return the raw text as the back side
            return (cleaned, "");
        }
    }

    /// <summary>
    /// Simple RFC-4180-compatible CSV line splitter for a given delimiter.
    /// </summary>
    private static string[] SplitCsvLine(string line, char delimiter)
    {
        var fields = new List<string>();
        var sb     = new StringBuilder();
        bool inQuotes = false;

        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];

            if (inQuotes)
            {
                if (c == '"')
                {
                    // Escaped quote?
                    if (i + 1 < line.Length && line[i + 1] == '"')
                    {
                        sb.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    sb.Append(c);
                }
            }
            else
            {
                if (c == '"')
                {
                    inQuotes = true;
                }
                else if (c == delimiter)
                {
                    fields.Add(sb.ToString());
                    sb.Clear();
                }
                else
                {
                    sb.Append(c);
                }
            }
        }

        fields.Add(sb.ToString());
        return [.. fields];
    }
}
