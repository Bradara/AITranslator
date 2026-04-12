using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HtmlAgilityPack;
using ReverseMarkdown;
using AITrans.Models;
using AITrans.Services;

namespace AITrans.ViewModels;

public partial class MarkdownViewModel : ViewModelBase
{
    private readonly TranslationService _translationService;
    private readonly SettingsService _settingsService;
    private readonly SpeechService _speechService;
    private readonly CacheService _cacheService;
    private CancellationTokenSource? _cts;
    private CancellationTokenSource? _speechCts;

    [ObservableProperty]
    private string _inputText = "";

    [ObservableProperty]
    private string _webUrl = "";

    [ObservableProperty]
    private ObservableCollection<MarkdownEntry> _paragraphs = [];

    [ObservableProperty]
    private bool _isTranslating;

    [ObservableProperty]
    private bool _isSpeaking;

    [ObservableProperty]
    private int _progress;

    [ObservableProperty]
    private string _selectedLanguage = "Bulgarian";

    [ObservableProperty]
    private string _statusText = "Ready";

    [ObservableProperty]
    private string? _loadedFilePath;

    [ObservableProperty]
    private string _cacheInfo = "";

    [ObservableProperty]
    private bool _hasCache;

    /// <summary>Set by commands to signal the view to scroll to a specific row index. View resets to -1 after handling.</summary>
    [ObservableProperty]
    private int _scrollToRow = -1;

    private List<int> _selectedIndices = [];
    private int _lastTranslatedIndex = -1;
    private int _lastSelectedIndex = -1;

    public string[] AvailableLanguages { get; } = ["Bulgarian", "Russian", "English"];

    public bool HasParagraphs => Paragraphs.Count > 0;

    internal CacheService CacheService => _cacheService;

    public MarkdownViewModel(TranslationService translationService, SettingsService settingsService, SpeechService speechService, CacheService cacheService)
    {
        _translationService = translationService;
        _settingsService = settingsService;
        _speechService = speechService;
        _cacheService = cacheService;
        SelectedLanguage = settingsService.Settings.DefaultLanguage;
        UpdateCacheInfo();
    }

    public void SetSelectedIndices(List<int> indices)
    {
        _selectedIndices = indices;
        if (_selectedIndices.Count > 0)
            UpdateLastSelectedIndex(_selectedIndices.Min());
    }

    public void LoadFile(string path)
    {
        var content = File.ReadAllText(path);
        InputText = content;
        LoadedFilePath = path;
        ParseParagraphs();
        ScrollToRow = GetRestoreRowIndex();
        StatusText = $"Loaded {Paragraphs.Count} paragraphs from {Path.GetFileName(path)}";
    }

    public void SaveTranslation(string path)
    {
        File.WriteAllText(path, GetCombinedTranslation());
        StatusText = $"Saved to {Path.GetFileName(path)}";
        // Cache is preserved so translation progress isn't lost
        UpdateCacheInfo();
    }

    public void SaveOriginal(string path)
    {
        var text = InputText;
        if (string.IsNullOrWhiteSpace(text) && Paragraphs.Count > 0)
            text = string.Join("\n\n", Paragraphs.Select(p => p.OriginalText));

        File.WriteAllText(path, text, System.Text.Encoding.UTF8);
        LoadedFilePath = path;
        StatusText = $"Original saved to {Path.GetFileName(path)}";
    }

    [RelayCommand]
    private void SaveCache()
    {
        if (Paragraphs.Count == 0) { StatusText = "Nothing to cache."; return; }
        var sessionKey = LoadedFilePath ?? "unsaved";
        _cacheService.SaveMarkdownSession(sessionKey, InputText, SelectedLanguage, Paragraphs);
        UpdateCacheInfo();
        StatusText = $"Session cached ({Paragraphs.Count} paragraphs).";
    }

    [RelayCommand]
    private void LoadCache()
    {
        var key = LoadedFilePath ?? "unsaved";
        if (_cacheService.GetMarkdownCacheInfo(key) == null)
            key = _cacheService.GetLatestMarkdownSession()?.SessionKey ?? "";
        if (string.IsNullOrEmpty(key)) { StatusText = "No cached session found."; return; }
        LoadCacheFromKey(key);
    }

    public void LoadCacheFromKey(string key)
    {
        var result = _cacheService.LoadMarkdownSession(key);
        if (result == null) { StatusText = "Cached session not found."; return; }

        var (inputText, paragraphs) = result.Value;
        InputText = inputText;
        if (key != "unsaved" && key != "current" && File.Exists(key))
            LoadedFilePath = key;
        Paragraphs = new ObservableCollection<MarkdownEntry>(paragraphs);
        OnPropertyChanged(nameof(HasParagraphs));
        var info = _cacheService.GetMarkdownCacheInfo(key);
        StatusText = $"Restored {paragraphs.Count} paragraphs from cache ({info?.TranslatedParagraphs}/{info?.TotalParagraphs} translated).";
        UpdateCacheInfo();
        ScrollToRow = GetRestoreRowIndex();
    }

    public void RefreshCacheInfo() => UpdateCacheInfo();

    public void RequestRestoreScroll()
    {
        if (Paragraphs.Count == 0) return;
        ScrollToRow = GetRestoreRowIndex();
    }

    private void UpdateCacheInfo()
    {
        var all = _cacheService.GetAllMarkdownSessions();
        if (all.Count == 0)
        {
            HasCache = false;
            CacheInfo = "";
            return;
        }
        HasCache = true;
        var key = LoadedFilePath ?? "unsaved";
        var info = all.Find(s => s.SessionKey == key) ?? all[0];
        CacheInfo = all.Count == 1
            ? $"Cached: {info.FileName} — {info.TranslatedParagraphs}/{info.TotalParagraphs} paragraphs — {info.SavedAt.ToLocalTime():HH:mm}"
            : $"Cached: {all.Count} sessions (latest: {info.FileName} — {info.SavedAt.ToLocalTime():HH:mm})";
    }

    [RelayCommand]
    private void ParseParagraphs()
    {
        if (string.IsNullOrWhiteSpace(InputText)) return;

        // Split on double newlines (blank lines) to get paragraphs.
        // Also filter short/decoration lines *within* each paragraph to avoid Avalonia
        // TextWrapping crash: "Cannot split: requested length N consumes entire run"
        static string CleanParagraph(string para)
        {
            var lines = para.Split('\n')
                .Select(l => l.Trim())
                .Where(l => l.Length >= 3)
                .Where(l => !System.Text.RegularExpressions.Regex.IsMatch(l, @"^[-*_|=\\s]+$"))
                .ToList();
            return string.Join("\n", lines).Trim();
        }

        var parts = InputText
            .Replace("\r\n", "\n")
            .Split(["\n\n"], StringSplitOptions.RemoveEmptyEntries)
            .Select(p => CleanParagraph(p))
            .Where(p => p.Length >= 3)
            .Where(p => !System.Text.RegularExpressions.Regex.IsMatch(p, @"^[-*_|=\\s]+$"))
            .ToList();

        var entries = new ObservableCollection<MarkdownEntry>();
        for (int i = 0; i < parts.Count; i++)
        {
            entries.Add(new MarkdownEntry { Index = i + 1, OriginalText = parts[i] });
        }

        Paragraphs = entries;
        ScrollToRow = GetRestoreRowIndex();
        StatusText = $"Parsed {parts.Count} paragraphs.";
        OnPropertyChanged(nameof(HasParagraphs));
    }

    [RelayCommand]
    private async Task ParseFromWebAsync()
    {
        if (string.IsNullOrWhiteSpace(WebUrl))
        {
            StatusText = "Please enter a web address.";
            return;
        }

        try
        {
            StatusText = "Fetching web page...";
            
            // Validate and normalize URL
            var url = WebUrl.Trim();
            if (!url.StartsWith("http://") && !url.StartsWith("https://"))
                url = "https://" + url;

            var handler = new System.Net.Http.HttpClientHandler
            {
                AllowAutoRedirect = true,
                UseCookies = true,
                CookieContainer = new System.Net.CookieContainer()
            };

            using (var client = new HttpClient(handler))
            {
                client.DefaultRequestHeaders.Clear();
                client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
                client.DefaultRequestHeaders.Add("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,image/webp,image/apng,*/*;q=0.8");
                client.DefaultRequestHeaders.Add("Accept-Language", "en-US,en;q=0.9,bg;q=0.8,ru;q=0.7");
                client.DefaultRequestHeaders.Add("Cache-Control", "no-cache");
                client.DefaultRequestHeaders.Add("Pragma", "no-cache");
                client.DefaultRequestHeaders.Add("Sec-Fetch-Dest", "document");
                client.DefaultRequestHeaders.Add("Sec-Fetch-Mode", "navigate");
                client.DefaultRequestHeaders.Add("Sec-Fetch-Site", "none");
                client.DefaultRequestHeaders.Add("DNT", "1");
                client.Timeout = TimeSpan.FromSeconds(30);
                
                HttpResponseMessage response;
                try
                {
                    response = await client.GetAsync(url);
                }
                catch (HttpRequestException ex) when (ex.Message.Contains("403"))
                {
                    StatusText = "Error 403: Website blocked the request. Try a different site.";
                    return;
                }

                if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
                {
                    StatusText = "Error 403: Website rejected the request (anti-bot protection). Try another site.";
                    return;
                }

                if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    StatusText = "Error 404: Page not found. Check the URL.";
                    return;
                }

                if (!response.IsSuccessStatusCode)
                {
                    StatusText = $"Error {(int)response.StatusCode}: {response.ReasonPhrase}. Could not fetch the page.";
                    return;
                }

                // Read raw bytes and detect encoding from HTML meta tags
                var rawBytes = await response.Content.ReadAsByteArrayAsync();
                var htmlContent = DetectEncodingAndDecode(rawBytes);

                if (string.IsNullOrWhiteSpace(htmlContent))
                {
                    StatusText = "Error: Website returned empty content.";
                    return;
                }

                StatusText = "Extracting article content...";
                
                var doc = new HtmlDocument();
                doc.LoadHtml(htmlContent);

                // All cleanup happens inside ExtractArticleAsMarkdown
                var markdown = ExtractArticleAsMarkdown(doc);

                if (string.IsNullOrWhiteSpace(markdown) || markdown.Length < 50)
                {
                    StatusText = "Warning: Very little content extracted. The page might not be readable.";
                    if (string.IsNullOrWhiteSpace(markdown)) return;
                }

                InputText = markdown;
                ParseParagraphs();

                StatusText = $"Successfully parsed web page ({Paragraphs.Count} paragraphs).";
            }
        }
        catch (TaskCanceledException)
        {
            StatusText = "Error: Request timed out (page took too long to load).";
        }
        catch (HttpRequestException ex)
        {
            StatusText = $"Error fetching web page: {ex.Message}";
        }
        catch (Exception ex)
        {
            StatusText = $"Error: {ex.Message}";
        }
    }

    /// <summary>Detect charset from HTML meta tags and decode raw bytes accordingly.</summary>
    private static string DetectEncodingAndDecode(byte[] rawBytes)
    {
        // First pass: do a rough ASCII/UTF-8 decode to find <meta charset="..."> or <meta http-equiv="Content-Type" content="...charset=...">
        var probe = Encoding.ASCII.GetString(rawBytes, 0, Math.Min(rawBytes.Length, 4096));
        
        Encoding encoding = Encoding.UTF8; // default

        // Look for <meta charset="windows-1251"> etc.
        var charsetMatch = Regex.Match(probe, @"<meta[^>]+charset\s*=\s*[""']?([^""'\s;>]+)", RegexOptions.IgnoreCase);
        if (charsetMatch.Success)
        {
            try { encoding = Encoding.GetEncoding(charsetMatch.Groups[1].Value); } catch { }
        }
        else
        {
            // Look for <meta http-equiv="Content-Type" content="text/html; charset=windows-1251">
            var contentTypeMatch = Regex.Match(probe, @"content=[""'][^""']*charset=([^""'\s;]+)", RegexOptions.IgnoreCase);
            if (contentTypeMatch.Success)
            {
                try { encoding = Encoding.GetEncoding(contentTypeMatch.Groups[1].Value); } catch { }
            }
        }

        return encoding.GetString(rawBytes);
    }

    private static readonly Converter MdConverter = new(new Config
    {
        UnknownTags = Config.UnknownTagsOption.Bypass,
        GithubFlavored = true,
        SmartHrefHandling = true,
        RemoveComments = true
    });

    /// <summary>Extract article content from parsed HTML as markdown.
    /// Uses ReverseMarkdown for structured HTML; falls back to text walker for br-only pages.</summary>
    private static string ExtractArticleAsMarkdown(HtmlDocument doc)
    {
        var body = doc.DocumentNode.SelectSingleNode("//body") ?? doc.DocumentNode;

        // Phase 1: Remove tags that can never contain readable text.
        RemoveTagsGlobally(body, SkipTags);

        // Phase 2: Try semantic containers BEFORE removing structural chrome.
        // This preserves <header>/<footer> inside <article> (e.g. article title, author info).
        var container = FindContentContainer(body);

        if (container == null)
        {
            // No semantic container found — remove structural chrome + noise divs,
            // then use text-density heuristic to find the best content block.
            RemoveTagsGlobally(body, ChromeTags);
            RemoveNoiseDivs(body);
            container = FindDensestTextBlock(body) ?? body;
        }

        // Phase 3: Detect structured vs. unstructured and convert.
        var structuredNodes = container.SelectNodes(
            ".//p | .//h1 | .//h2 | .//h3 | .//h4 | .//h5 | .//h6 | .//ul | .//ol | .//blockquote | .//table");
        bool isStructured = structuredNodes != null && structuredNodes.Count >= 2;

        string markdown = isStructured
            ? MdConverter.Convert(container.OuterHtml)
            : ExtractUnstructuredText(container);

        return CleanMarkdown(markdown);
    }

    /// <summary>Try semantic HTML selectors to find the article container.
    /// Returns null if nothing matches or matched node has too little text.</summary>
    private static HtmlNode? FindContentContainer(HtmlNode body)
    {
        string[] selectors =
        {
            ".//article",
            ".//main",
            ".//*[@role='main']",
            ".//div[@itemprop='articleBody']",
            ".//div[contains(@class,'article-body')]",
            ".//div[contains(@class,'article-content')]",
            ".//div[contains(@class,'article-text')]",
            ".//div[contains(@class,'post-content')]",
            ".//div[contains(@class,'post-body')]",
            ".//div[contains(@class,'entry-content')]",
            ".//div[contains(@class,'entry-body')]",
            ".//div[contains(@class,'story-body')]",
            ".//div[contains(@class,'page-content')]",
        };

        foreach (var sel in selectors)
        {
            var node = body.SelectSingleNode(sel);
            if (node != null && (node.InnerText?.Trim().Length ?? 0) >= 50)
                return node;
        }

        return null;
    }

    /// <summary>Remove divs/sections that are common noise based on class/id patterns.</summary>
    private static void RemoveNoiseDivs(HtmlNode root)
    {
        string[] noisePatterns =
        {
            "cookie", "consent", "gdpr", "popup", "modal", "overlay",
            "sidebar", "widget", "comment", "menu", "toolbar",
            "social", "share", "related", "recommend", "promo",
            "advert", "sponsor", "newsletter", "signup", "subscribe",
            "banner", "notification", "alert"
        };

        var toRemove = root.Descendants()
            .Where(n => n.NodeType == HtmlNodeType.Element &&
                        n.Name is "div" or "section" or "aside" or "ul")
            .Where(n =>
            {
                var cls = n.GetAttributeValue("class", "").ToLowerInvariant();
                var id = n.GetAttributeValue("id", "").ToLowerInvariant();
                return noisePatterns.Any(p => cls.Contains(p) || id.Contains(p));
            })
            .ToList();

        foreach (var n in toRemove)
            n.Remove();
    }

    /// <summary>Find the container with the highest text density.
    /// Uses textLen²/htmlLen scoring — rewards deep containers with lots of text and little markup.</summary>
    private static HtmlNode? FindDensestTextBlock(HtmlNode root)
    {
        HtmlNode? best = null;
        double bestScore = 0;

        foreach (var node in root.Descendants().Where(n =>
            n.NodeType == HtmlNodeType.Element &&
            n.Name is "div" or "section" or "td"))
        {
            var textLen = (node.InnerText?.Trim() ?? "").Length;
            if (textLen < 200) continue;

            var htmlLen = node.InnerHtml.Length;
            if (htmlLen == 0) continue;

            // textLen²/htmlLen prefers deep containers with content over shallow wrappers.
            var score = (double)textLen * textLen / htmlLen;

            if (score > bestScore)
            {
                bestScore = score;
                best = node;
            }
        }

        return best;
    }

    /// <summary>Remove all nodes matching the given tag set from the subtree.</summary>
    private static void RemoveTagsGlobally(HtmlNode root, HashSet<string> tags)
    {
        var toRemove = root.Descendants()
            .Where(n => n.NodeType == HtmlNodeType.Element && tags.Contains(n.Name))
            .ToList();
        foreach (var n in toRemove)
            n.Remove();
    }

    /// <summary>Extract plain text from unstructured content (br-separated), walking text nodes.</summary>
    private static string ExtractUnstructuredText(HtmlNode container)
    {
        var html = container.InnerHtml;
        html = Regex.Replace(html, @"<br\s*/?\s*>", "\n", RegexOptions.IgnoreCase);

        var tempDoc = new HtmlDocument();
        tempDoc.LoadHtml($"<div>{html}</div>");
        var root = tempDoc.DocumentNode.SelectSingleNode("//div") ?? tempDoc.DocumentNode;

        var sb = new StringBuilder();
        ExtractTextRecursive(root, sb);

        var paragraphs = Regex.Split(sb.ToString(), @"\n\s*\n")
            .Select(p => p.Trim())
            .Where(p => p.Length >= 3)
            .Where(p => !Regex.IsMatch(p, @"^[-*_|=\s]+$"))
            .ToList();

        return string.Join("\n\n", paragraphs);
    }

    // Tags that can never contain readable article text
    private static readonly HashSet<string> SkipTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "script", "style", "noscript", "svg", "iframe", "button", "input",
        "select", "textarea", "img", "video", "audio", "canvas", "map",
        "object", "embed", "picture", "source", "track"
    };

    // Semantic HTML5 chrome tags — always site wrapping, never article body
    private static readonly HashSet<string> ChromeTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "nav", "header", "footer", "aside", "form"
    };

    /// <summary>Recursively walks DOM nodes and collects visible text.
    /// Called only on unstructured (br-separated) content after SkipTags are already removed.</summary>
    private static void ExtractTextRecursive(HtmlNode node, StringBuilder sb)
    {
        if (node.NodeType == HtmlNodeType.Element)
        {
            var tag = node.Name.ToLowerInvariant();

            // SkipTags are already removed globally before this is called, but guard here too
            if (SkipTags.Contains(tag))
                return;

            // Block-level elements
            var isBlock = tag is "div" or "p" or "h1" or "h2" or "h3" or "h4" or "h5" or "h6"
                or "li" or "tr" or "blockquote" or "section" or "article" or "main"
                or "pre" or "table" or "dl" or "dt" or "dd" or "figure" or "details"
                or "summary" or "address" or "fieldset" or "hr";

            if (tag is "h1" or "h2" or "h3" or "h4" or "h5" or "h6")
            {
                var level = tag[1] - '0';
                sb.AppendLine().AppendLine();
                sb.Append(new string('#', level) + " ");
            }
            else if (tag == "li")
            {
                sb.AppendLine();
                sb.Append("- ");
            }
            else if (isBlock)
            {
                sb.AppendLine().AppendLine();
            }

            foreach (var child in node.ChildNodes)
                ExtractTextRecursive(child, sb);

            if (isBlock)
                sb.AppendLine().AppendLine();
        }
        else if (node.NodeType == HtmlNodeType.Text)
        {
            var text = HtmlEntity.DeEntitize(node.InnerText);
            if (!string.IsNullOrWhiteSpace(text))
                sb.Append(text);
        }
        else
        {
            foreach (var child in node.ChildNodes)
                ExtractTextRecursive(child, sb);
        }
    }

    private static string CleanMarkdown(string markdown)
    {
        // Remove excessive blank lines
        markdown = Regex.Replace(markdown, @"\n\n\n+", "\n\n");
        // Remove lines that are only whitespace/decoration
        markdown = Regex.Replace(markdown, @"^\s*[-*_]{1,2}\s*$", "", RegexOptions.Multiline);
        // Clean up excessive blank lines again after removal
        markdown = Regex.Replace(markdown, @"\n\n\n+", "\n\n");
        return markdown.Trim();
    }

    [RelayCommand]
    private async Task TranslateAsync()
    {
        if (Paragraphs.Count == 0)
        {
            ParseParagraphs();
            if (Paragraphs.Count == 0) return;
        }
        await TranslateRangeAsync(0, Paragraphs.Count - 1);
    }

    [RelayCommand]
    private async Task TranslateSelectedAsync()
    {
        if (_selectedIndices.Count == 0)
        {
            StatusText = "No rows selected.";
            return;
        }
        await TranslateIndicesAsync(_selectedIndices);
    }

    [RelayCommand]
    private async Task TranslateFromSelectedAsync()
    {
        if (_selectedIndices.Count == 0)
        {
            StatusText = "No row selected.";
            return;
        }
        var fromIndex = _selectedIndices.Min();
        await TranslateRangeAsync(fromIndex, Paragraphs.Count - 1);
    }

    [RelayCommand]
    private void CancelTranslation()
    {
        _cts?.Cancel();
    }

    [RelayCommand]
    private async Task ReadOriginalAsync()
    {
        var settings = _settingsService.Settings;
        if (string.IsNullOrWhiteSpace(settings.AzureSpeechApiKey) || string.IsNullOrWhiteSpace(settings.AzureSpeechRegion))
        {
            StatusText = "Azure Speech not configured. Go to Settings tab.";
            return;
        }
        if (Paragraphs.Count == 0) return;

        var fromIndex = _selectedIndices.Count > 0 ? _selectedIndices.Min() : 0;
        var texts = Paragraphs.Skip(fromIndex).Select(p => p.OriginalText).Where(t => !string.IsNullOrWhiteSpace(t)).ToList();
        if (texts.Count == 0) return;

        IsSpeaking = true;
        _speechCts = new CancellationTokenSource();
        StatusText = fromIndex > 0 ? $"Reading original from paragraph {fromIndex + 1}..." : "Reading original...";

        try
        {
            await _speechService.SpeakParagraphsAsync(
                texts, settings.SpeechSourceLanguage,
                settings.AzureSpeechApiKey, settings.AzureSpeechRegion, _speechCts.Token);
            StatusText = "Done reading.";
        }
        catch (OperationCanceledException)
        {
            StatusText = "Reading stopped.";
        }
        catch (Exception ex)
        {
            StatusText = $"Speech error: {ex.Message}";
        }
        finally
        {
            IsSpeaking = false;
            _speechCts?.Dispose();
            _speechCts = null;
        }
    }

    [RelayCommand]
    private async Task ReadTranslationAsync()
    {
        var settings = _settingsService.Settings;
        if (string.IsNullOrWhiteSpace(settings.AzureSpeechApiKey) || string.IsNullOrWhiteSpace(settings.AzureSpeechRegion))
        {
            StatusText = "Azure Speech not configured. Go to Settings tab.";
            return;
        }
        if (Paragraphs.Count == 0) return;

        var fromIndex = _selectedIndices.Count > 0 ? _selectedIndices.Min() : 0;
        var texts = Paragraphs.Skip(fromIndex)
            .Select(p => !string.IsNullOrEmpty(p.TranslatedText) ? p.TranslatedText : p.OriginalText)
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .ToList();
        if (texts.Count == 0) return;

        IsSpeaking = true;
        _speechCts = new CancellationTokenSource();
        StatusText = fromIndex > 0 ? $"Reading translation from paragraph {fromIndex + 1}..." : "Reading translation...";

        try
        {
            await _speechService.SpeakParagraphsAsync(
                texts, SelectedLanguage,
                settings.AzureSpeechApiKey, settings.AzureSpeechRegion, _speechCts.Token);
            StatusText = "Done reading.";
        }
        catch (OperationCanceledException)
        {
            StatusText = "Reading stopped.";
        }
        catch (Exception ex)
        {
            StatusText = $"Speech error: {ex.Message}";
        }
        finally
        {
            IsSpeaking = false;
            _speechCts?.Dispose();
            _speechCts = null;
        }
    }

    [RelayCommand]
    private void StopSpeech()
    {
        _speechCts?.Cancel();
        _speechService.Stop();
    }

    /// <summary>
    /// Combines all translations back into one text block.
    /// </summary>
    public string GetCombinedTranslation()
    {
        return string.Join("\n\n", Paragraphs
            .Select(p => !string.IsNullOrEmpty(p.TranslatedText) ? p.TranslatedText : p.OriginalText));
    }

    private async Task TranslateRangeAsync(int from, int to)
    {
        var indices = Enumerable.Range(from, to - from + 1).ToList();
        await TranslateIndicesAsync(indices);
    }

    private async Task TranslateIndicesAsync(List<int> indices)
    {
        if (Paragraphs.Count == 0 || indices.Count == 0) return;

        var settings = _settingsService.Settings;

        IsTranslating = true;
        Progress = 0;
        _cts = new CancellationTokenSource();

        int translated = 0;
        var total = indices.Count;
        StatusText = $"Translating {total} paragraphs...";

        try
        {
            var indexMap = indices.Where(i => i >= 0 && i < Paragraphs.Count).ToList();
            var texts = indexMap.Select(i => Paragraphs[i].OriginalText).ToList();

            var progressReporter = new Progress<int>(p => Progress = p);

            void OnEntryTranslated(int batchIdx, string text)
            {
                if (batchIdx >= 0 && batchIdx < indexMap.Count)
                {
                    var realIdx = indexMap[batchIdx];
                    Paragraphs[realIdx].TranslatedText = text;
                    SetLastTranslatedIndex(realIdx);
                    translated++;
                    StatusText = $"Translated {translated}/{total}...";
                }
            }

            if (settings.UseAzureTranslatorForMarkdown)
            {
                if (string.IsNullOrWhiteSpace(settings.AzureTranslatorApiKey))
                {
                    StatusText = "Error: Azure Translator API key not set. Go to Settings tab.";
                    return;
                }

                var translations = await _translationService.TranslateAzureTranslatorBatchAsync(
                    texts, SelectedLanguage,
                    settings.AzureTranslatorApiKey, settings.AzureTranslatorEndpoint, settings.AzureTranslatorRegion,
                    progressReporter, OnEntryTranslated, _cts.Token);

                for (int i = 0; i < indexMap.Count && i < translations.Count; i++)
                {
                    var realIdx = indexMap[i];
                    if (!string.IsNullOrEmpty(translations[i]) && string.IsNullOrEmpty(Paragraphs[realIdx].TranslatedText))
                        Paragraphs[realIdx].TranslatedText = translations[i];
                }
            }
            else if (settings.UseDeepLForMarkdown)
            {
                if (string.IsNullOrWhiteSpace(settings.DeepLApiKey))
                {
                    StatusText = "Error: DeepL API key not set. Go to Settings tab.";
                    return;
                }

                var translations = await _translationService.TranslateDeepLBatchAsync(
                    texts, SelectedLanguage, settings.DeepLApiKey, settings.DeepLFreeApi,
                    progressReporter, OnEntryTranslated, _cts.Token, settings.DelayBetweenRequestsMs);

                for (int i = 0; i < indexMap.Count && i < translations.Count; i++)
                {
                    var realIdx = indexMap[i];
                    if (!string.IsNullOrEmpty(translations[i]) && string.IsNullOrEmpty(Paragraphs[realIdx].TranslatedText))
                        Paragraphs[realIdx].TranslatedText = translations[i];
                }
            }
            else
            {
                var apiKey = settings.ActiveApiKey;
                if (string.IsNullOrWhiteSpace(apiKey))
                {
                    StatusText = "Error: API key not set. Go to Settings tab.";
                    return;
                }

                // Use the dedicated markdown batch size setting
                var batchSize = settings.MarkdownBatchSize > 0 ? settings.MarkdownBatchSize : 10;

                var translations = await _translationService.TranslateSubtitleBatchAsync(
                    texts, SelectedLanguage, apiKey, settings.ActiveModel, settings.ActiveEndpoint,
                    batchSize, settings.DelayBetweenRequestsMs,
                    progressReporter, OnEntryTranslated, settings, _cts.Token);

                for (int i = 0; i < indexMap.Count && i < translations.Count; i++)
                {
                    var realIdx = indexMap[i];
                    if (!string.IsNullOrEmpty(translations[i]) && string.IsNullOrEmpty(Paragraphs[realIdx].TranslatedText))
                        Paragraphs[realIdx].TranslatedText = translations[i];
                }
            }

            StatusText = $"Done. {translated} of {total} paragraphs translated.";
        }
        catch (OperationCanceledException)
        {
            StatusText = $"Cancelled. {translated} of {total} paragraphs translated.";
        }
        catch (Exception ex)
        {
            StatusText = $"Error after {translated}/{total}: {ex.Message}";
        }
        finally
        {
            IsTranslating = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    public void PersistSessionState()
    {
        if (Paragraphs.Count == 0) return;
        var key = GetSessionKey();
        var lastIdx = GetLastTranslatedIndexFromParagraphs();
        if (!string.IsNullOrWhiteSpace(key) && lastIdx >= 0)
            _settingsService.Settings.MarkdownLastTranslatedIndexByFile[key] = lastIdx;
        if (!string.IsNullOrWhiteSpace(key) && _lastSelectedIndex >= 0)
            _settingsService.Settings.MarkdownLastSelectedIndexByFile[key] = _lastSelectedIndex;
        _settingsService.Save();
    }

    private string GetSessionKey() => LoadedFilePath ?? "unsaved";

    private int GetLastTranslatedIndexFromParagraphs()
    {
        for (int i = Paragraphs.Count - 1; i >= 0; i--)
        {
            if (!string.IsNullOrEmpty(Paragraphs[i].TranslatedText))
                return i;
        }
        return -1;
    }

    private void SetLastTranslatedIndex(int idx)
    {
        if (idx < 0) return;
        _lastTranslatedIndex = Math.Max(_lastTranslatedIndex, idx);
        var key = GetSessionKey();
        if (!string.IsNullOrWhiteSpace(key))
            _settingsService.Settings.MarkdownLastTranslatedIndexByFile[key] = _lastTranslatedIndex;
    }

    private void UpdateLastSelectedIndex(int idx)
    {
        if (idx < 0) return;
        _lastSelectedIndex = idx;
        var key = GetSessionKey();
        if (!string.IsNullOrWhiteSpace(key))
            _settingsService.Settings.MarkdownLastSelectedIndexByFile[key] = _lastSelectedIndex;
    }

    private int GetRestoreRowIndex()
    {
        if (Paragraphs.Count == 0) return -1;
        var key = GetSessionKey();
        if (_settingsService.Settings.MarkdownLastSelectedIndexByFile.TryGetValue(key, out var selectedIdx)
            && selectedIdx >= 0)
        {
            _lastSelectedIndex = selectedIdx;
            return Math.Clamp(selectedIdx, 0, Paragraphs.Count - 1);
        }
        if (!_settingsService.Settings.MarkdownLastTranslatedIndexByFile.TryGetValue(key, out var lastIdx))
            lastIdx = GetLastTranslatedIndexFromParagraphs();
        _lastTranslatedIndex = lastIdx;
        if (lastIdx >= 0 && !string.IsNullOrWhiteSpace(key))
            _settingsService.Settings.MarkdownLastTranslatedIndexByFile[key] = lastIdx;
        if (lastIdx < 0) return 0;
        return Math.Min(lastIdx + 1, Paragraphs.Count - 1);
    }
}
