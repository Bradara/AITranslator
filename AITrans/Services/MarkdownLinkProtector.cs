using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace AITrans.Services;

/// <summary>
/// Shields markdown link/image URLs from translation. Translation providers (AI models, DeepL,
/// Azure Translator, Google Translate) treat the whole paragraph as natural-language text, so a
/// path segment like "images" in ![](../images/00001.jpeg) gets translated along with the prose,
/// breaking the file reference. Protect() swaps each URL for a placeholder the translator has no
/// reason to touch; Restore() puts the original URLs back afterwards.
/// </summary>
public static class MarkdownLinkProtector
{
    // Matches inline links/images: ![alt](url "title") or [text](url "title").
    // The URL itself (no parens/whitespace) is captured separately from the optional title.
    private static readonly Regex InlineLinkPattern = new(
        @"(!?\[[^\]]*\]\()([^()\s]+)((?:\s+""[^""]*"")?)\)",
        RegexOptions.Compiled);

    private static readonly Regex PlaceholderPattern = new(@"<x(\d+)/>", RegexOptions.Compiled);

    /// <summary>Replaces every markdown link/image URL in <paramref name="text"/> with a
    /// "&lt;x0/&gt;"-style placeholder. Returns the modified text plus the extracted URLs, in
    /// placeholder order, needed to restore them later.</summary>
    public static (string Text, List<string> Urls) Protect(string text)
    {
        if (string.IsNullOrEmpty(text)) return (text, []);

        var urls = new List<string>();
        var protectedText = InlineLinkPattern.Replace(text, match =>
        {
            var placeholder = $"<x{urls.Count}/>";
            urls.Add(match.Groups[2].Value);
            return $"{match.Groups[1].Value}{placeholder}{match.Groups[3].Value})";
        });

        return (protectedText, urls);
    }

    /// <summary>Replaces "&lt;x0/&gt;"-style placeholders in translated text with the original
    /// URLs captured by <see cref="Protect"/>. Placeholders the translator dropped or mangled are
    /// left as-is rather than guessed at.</summary>
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
