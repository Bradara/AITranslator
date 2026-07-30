using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace AITrans.Services;

/// <summary>
/// Shields markdown link/image URLs from translation. Translation providers (AI models, DeepL,
/// Azure Translator, Google Translate) treat the whole paragraph as natural-language text, so a
/// path segment like "images" in ![](../images/00001.jpeg) gets translated along with the prose,
/// breaking the file reference. Protect() swaps each URL for a placeholder the translator has no
/// reason to touch; Restore() puts the original URLs back afterwards. IsLinkOnly() flags paragraphs
/// that are nothing but a link/image, so callers can skip the translation round-trip entirely.
/// </summary>
public static class MarkdownLinkProtector
{
    // Matches inline links/images: ![alt](url "title") or [text](url "title").
    // The URL group only excludes ')' (lazily, so an optional quoted title still splits off
    // correctly) — real-world local paths (e.g. from ebook imports) often contain spaces.
    private static readonly Regex InlineLinkPattern = new(
        @"(!?\[[^\]]*\]\()([^)]+?)((?:\s+""[^""]*"")?)\)",
        RegexOptions.Compiled);

    // Plain alphanumeric token — no angle brackets or other characters an HTML-aware layer
    // (some translation APIs escape '<'/'>' as &lt;/&gt; in their response) could mangle.
    private static readonly Regex PlaceholderPattern = new(@"zzlink(\d+)zz", RegexOptions.Compiled);

    /// <summary>True when the whole text is nothing but link(s)/image(s) — no other translatable
    /// content — so it can be copied through as-is instead of being sent to a translator at all.</summary>
    public static bool IsLinkOnly(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        return string.IsNullOrWhiteSpace(InlineLinkPattern.Replace(text, ""));
    }

    /// <summary>Replaces every markdown link/image URL in <paramref name="text"/> with a
    /// "zzlink0zz"-style placeholder. Returns the modified text plus the extracted URLs, in
    /// placeholder order, needed to restore them later.</summary>
    public static (string Text, List<string> Urls) Protect(string text)
    {
        if (string.IsNullOrEmpty(text)) return (text, []);

        var urls = new List<string>();
        var protectedText = InlineLinkPattern.Replace(text, match =>
        {
            var placeholder = $"zzlink{urls.Count}zz";
            urls.Add(match.Groups[2].Value);
            return $"{match.Groups[1].Value}{placeholder}{match.Groups[3].Value})";
        });

        return (protectedText, urls);
    }

    /// <summary>Replaces "zzlink0zz"-style placeholders in translated text with the original URLs
    /// captured by <see cref="Protect"/>. Placeholders the translator dropped or mangled are left
    /// as-is rather than guessed at.</summary>
    public static string Restore(string text, List<string> urls)
    {
        if (string.IsNullOrEmpty(text) || urls.Count == 0) return text;

        return PlaceholderPattern.Replace(text, match =>
        {
            var idx = int.Parse(match.Groups[1].Value);
            return idx >= 0 && idx < urls.Count ? urls[idx] : match.Value;
        });
    }
}
