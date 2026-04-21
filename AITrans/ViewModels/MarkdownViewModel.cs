using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
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
    private readonly EbookImportService _ebookImportService;
    private CancellationTokenSource? _cts;
    private CancellationTokenSource? _speechCts;

    private const string WebAssetsDirName = "web-assets";
    private string? _webAssetsTempRoot;
    private readonly List<WebAssetFile> _webAssets = [];
    private int _lastWebImagesDownloaded;
    private int _lastWebImagesSkipped;
    private int _lastWebImagesFailed;

    private sealed record WebAssetFile(string FileName, string TempPath);
    private sealed record ImagePrepareSummary(int Downloaded, int Skipped, int Failed);
    private sealed record ArticleExtractResult(string Markdown, string? Title, ImagePrepareSummary ImageSummary);

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
    private bool _isSpeechPaused;

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

    public MarkdownViewModel(TranslationService translationService, SettingsService settingsService, SpeechService speechService, CacheService cacheService, EbookImportService ebookImportService)
    {
        _translationService = translationService;
        _settingsService = settingsService;
        _speechService = speechService;
        _cacheService = cacheService;
        _ebookImportService = ebookImportService;
        SelectedLanguage = settingsService.Settings.DefaultLanguage;
        UpdateCacheInfo();
    }

    public string EbookWorkingFolder => _settingsService.Settings.EbookWorkingFolder;

    public void UpdateEbookWorkingFolder(string folderPath)
    {
        _settingsService.Settings.EbookWorkingFolder = folderPath ?? "";
        _settingsService.Save();
    }

    public void SetSelectedIndices(List<int> indices)
    {
        _selectedIndices = indices;
        if (_selectedIndices.Count > 0)
            UpdateLastSelectedIndex(_selectedIndices.Min());
    }

    [RelayCommand]
    private void DeleteSelected()
    {
        if (Paragraphs.Count == 0 || _selectedIndices.Count == 0)
        {
            StatusText = "No rows selected.";
            return;
        }

        var indices = _selectedIndices
            .Distinct()
            .Where(i => i >= 0 && i < Paragraphs.Count)
            .OrderByDescending(i => i)
            .ToList();

        if (indices.Count == 0)
        {
            StatusText = "No rows selected.";
            return;
        }

        var firstRemoved = indices.Min();
        foreach (var idx in indices)
            Paragraphs.RemoveAt(idx);

        _selectedIndices.Clear();
        _lastSelectedIndex = -1;

        ReindexParagraphs();
        SyncInputTextFromParagraphs();
        OnPropertyChanged(nameof(HasParagraphs));

        StatusText = $"Deleted {indices.Count} paragraph(s).";
        if (Paragraphs.Count > 0)
            ScrollToRow = Math.Min(firstRemoved, Paragraphs.Count - 1);
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
        var text = GetCombinedTranslation();
        File.WriteAllText(path, text);
        var assetNote = FinalizeWebAssetsForPath(path);
        StatusText = $"Saved to {Path.GetFileName(path)}{assetNote}";
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
        var assetNote = FinalizeWebAssetsForPath(path);
        StatusText = $"Original saved to {Path.GetFileName(path)}{assetNote}";
    }

    public async Task ImportEbookAsync(string sourcePath, string outputRoot)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            StatusText = "Import canceled.";
            return;
        }

        if (string.IsNullOrWhiteSpace(outputRoot))
        {
            StatusText = "Working folder not set.";
            return;
        }

        try
        {
            StatusText = "Importing ebook...";
            var result = await _ebookImportService.ImportAsync(sourcePath, outputRoot, CancellationToken.None);
            InputText = result.Markdown;
            LoadedFilePath = result.MarkdownPath;
            ParseParagraphs();
            StatusText = $"Imported {Paragraphs.Count} paragraphs, {result.ImageCount} images.";
        }
        catch (Exception ex)
        {
            StatusText = $"Import failed: {ex.Message}";
        }
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

    private void ReindexParagraphs()
    {
        for (int i = 0; i < Paragraphs.Count; i++)
            Paragraphs[i].Index = i + 1;
    }

    private void SyncInputTextFromParagraphs()
    {
        InputText = Paragraphs.Count == 0
            ? ""
            : string.Join("\n\n", Paragraphs.Select(p => p.OriginalText));
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

            ResetWebAssetsState();
            
            // Validate and normalize URL
            var url = WebUrl.Trim();
            if (!url.StartsWith("http://") && !url.StartsWith("https://"))
                url = "https://" + url;

            var baseUri = new Uri(url);

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

                baseUri = response.RequestMessage?.RequestUri ?? baseUri;

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

                // All cleanup happens inside ExtractArticleAsMarkdownAsync
                var result = await ExtractArticleAsMarkdownAsync(doc, baseUri, client);
                var markdown = result.Markdown;

                _lastWebImagesDownloaded = result.ImageSummary.Downloaded;
                _lastWebImagesSkipped = result.ImageSummary.Skipped;
                _lastWebImagesFailed = result.ImageSummary.Failed;

                if (string.IsNullOrWhiteSpace(markdown) || markdown.Length < 50)
                {
                    StatusText = "Warning: Very little content extracted. The page might not be readable.";
                    if (string.IsNullOrWhiteSpace(markdown)) return;
                }

                InputText = markdown;
                ParseParagraphs();

                var imageNote = BuildImageStatusText(result.ImageSummary);
                StatusText = $"Successfully parsed web page ({Paragraphs.Count} paragraphs).{imageNote}";
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

    private void ResetWebAssetsState()
    {
        if (!string.IsNullOrWhiteSpace(_webAssetsTempRoot) && Directory.Exists(_webAssetsTempRoot))
        {
            try { Directory.Delete(_webAssetsTempRoot, true); } catch { }
        }

        _webAssetsTempRoot = null;
        _webAssets.Clear();
        _lastWebImagesDownloaded = 0;
        _lastWebImagesSkipped = 0;
        _lastWebImagesFailed = 0;
    }

    private static string BuildImageStatusText(ImagePrepareSummary summary)
    {
        if (summary.Downloaded == 0 && summary.Skipped == 0 && summary.Failed == 0)
            return "";

        var parts = new List<string>();
        if (summary.Downloaded > 0) parts.Add($"downloaded {summary.Downloaded}");
        if (summary.Skipped > 0) parts.Add($"skipped {summary.Skipped}");
        if (summary.Failed > 0) parts.Add($"failed {summary.Failed}");
        return " Images: " + string.Join(", ", parts) + ".";
    }

    private static string ComposeFinalMarkdown(string? title, string body)
    {
        if (string.IsNullOrWhiteSpace(title))
            return body;

        var cleanTitle = NormalizeInlineText(title);
        if (string.IsNullOrWhiteSpace(cleanTitle))
            return body;

        if (string.IsNullOrWhiteSpace(body))
            return "# " + cleanTitle;

        return "# " + cleanTitle + "\n\n" + body;
    }

    private static string NormalizeInlineText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        text = HtmlEntity.DeEntitize(text);
        text = Regex.Replace(text, "\\s+", " ").Trim();
        return text;
    }

    private string EnsureWebAssetsTempRoot(Uri baseUri)
    {
        if (!string.IsNullOrWhiteSpace(_webAssetsTempRoot))
            return _webAssetsTempRoot;

        var safeHost = SanitizeFileName(baseUri.Host);
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "AITrans", "web-assets-temp", safeHost, stamp);
        Directory.CreateDirectory(dir);
        _webAssetsTempRoot = dir;
        return dir;
    }

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(value.Length);
        foreach (var c in value)
            sb.Append(invalid.Contains(c) ? '_' : c);
        return sb.ToString();
    }

    private static string BuildFileName(string key, string extension)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        var hashText = Convert.ToHexString(hash).ToLowerInvariant().Substring(0, 12);
        return $"img_{hashText}{extension}";
    }

    private string FinalizeWebAssetsForPath(string path)
    {
        if (_webAssets.Count == 0 || string.IsNullOrWhiteSpace(_webAssetsTempRoot))
            return "";

        try
        {
            var dir = Path.GetDirectoryName(path);
            if (string.IsNullOrWhiteSpace(dir))
                return "";

            var targetDir = Path.Combine(dir, WebAssetsDirName);
            Directory.CreateDirectory(targetDir);

            int copied = 0;
            int missing = 0;
            foreach (var asset in _webAssets)
            {
                var dest = Path.Combine(targetDir, asset.FileName);
                if (!File.Exists(asset.TempPath))
                {
                    missing++;
                    continue;
                }

                File.Copy(asset.TempPath, dest, true);
                copied++;
            }

            if (copied == 0 && missing == 0)
                return "";

            return $" (images copied: {copied}, missing: {missing})";
        }
        catch (Exception ex)
        {
            return $" (image copy failed: {ex.Message})";
        }
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
    private async Task<ArticleExtractResult> ExtractArticleAsMarkdownAsync(HtmlDocument doc, Uri baseUri, HttpClient client)
    {
        var body = doc.DocumentNode.SelectSingleNode("//body") ?? doc.DocumentNode;

        // Phase 1: Remove tags that can never contain readable text.
        RemoveTagsGlobally(body, SkipTags);

        // Phase 2: Try semantic containers BEFORE removing structural chrome.
        var container = FindMainContainer(body);
        var fromMain = container != null;
        var fromMainArticles = false;

        if (container == null)
        {
            container = BuildMainArticlesContainer(body, out fromMainArticles);
        }

        if (container == null)
        {
            container = FindContentContainer(body);
        }

        if (container == null)
        {
            RemoveTagsGlobally(body, ChromeTags);
            RemoveNoiseDivs(body);
            container = FindDensestTextBlock(body) ?? body;
        }
        else if (!fromMain && !fromMainArticles)
        {
            var containerTextLen = NormalizeInlineText(container.InnerText).Length;
            if (containerTextLen < 200)
            {
                var dense = FindDensestTextBlock(body);
                if (dense != null)
                {
                    var denseLen = NormalizeInlineText(dense.InnerText).Length;
                    if (denseLen > Math.Max(400, containerTextLen * 2))
                        container = dense;
                }
            }
        }

        var title = ExtractTitle(doc, container);

        // Remove chrome + noisy blocks inside the article after title extraction.
        RemoveTagsGlobally(container, ChromeTags);
        RemoveNoiseDivs(container);
        RemoveArticleNoiseBlocks(container);
        if (fromMain)
            RemoveMainNoiseBlocks(container);

        // Normalize images and download assets before conversion.
        var imageSummary = await PrepareImagesAsync(container, baseUri, client);
        NormalizeFigures(container);

        // Phase 3: Detect structured vs. unstructured and convert.
        var structuredNodes = container.SelectNodes(
            ".//p | .//h1 | .//h2 | .//h3 | .//h4 | .//h5 | .//h6 | .//ul | .//ol | .//blockquote | .//table");
        bool isStructured = structuredNodes != null && structuredNodes.Count >= 2;

        string bodyMarkdown = isStructured
            ? MdConverter.Convert(container.OuterHtml)
            : ExtractUnstructuredText(container);

        bodyMarkdown = CleanMarkdown(bodyMarkdown);
        var markdown = ComposeFinalMarkdown(title, bodyMarkdown);

        return new ArticleExtractResult(markdown, title, imageSummary);
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
            ".//section[contains(@class,'content')]",
            ".//section[contains(@class,'article')]",
        };

        HtmlNode? bestNode = null;
        double bestScore = 0;

        foreach (var sel in selectors)
        {
            var nodes = body.SelectNodes(sel);
            if (nodes == null) continue;

            foreach (var node in nodes)
            {
                var text = NormalizeInlineText(node.InnerText);
                var textLen = text.Length;
                if (textLen < 50) continue;

                var htmlLen = node.InnerHtml?.Length ?? 0;
                if (htmlLen <= 0) continue;

                var score = (double)textLen * textLen / htmlLen;
                if (score > bestScore)
                {
                    bestScore = score;
                    bestNode = node;
                }
            }
        }

        return bestNode;
    }

    /// <summary>Prefer extracting the entire &lt;main&gt; content when present.</summary>
    private static HtmlNode? FindMainContainer(HtmlNode body)
    {
        var main = body.SelectSingleNode(".//main");
        if (main == null) return null;

        var textLen = NormalizeInlineText(main.InnerText).Length;
        return textLen >= 100 ? main : null;
    }

    /// <summary>Combine all article nodes inside &lt;main&gt; into a single container.</summary>
    private static HtmlNode? BuildMainArticlesContainer(HtmlNode body, out bool fromMainArticles)
    {
        fromMainArticles = false;
        var main = body.SelectSingleNode(".//main");
        if (main == null) return null;

        var articles = main.SelectNodes(".//article")?.ToList();
        if (articles == null || articles.Count == 0) return null;

        var meaningful = articles
            .Where(a => NormalizeInlineText(a.InnerText).Length >= 50)
            .ToList();

        if (meaningful.Count == 0) return null;
        if (meaningful.Count == 1) return meaningful[0];

        var wrapper = main.OwnerDocument.CreateElement("div");
        foreach (var article in meaningful)
            wrapper.AppendChild(article.CloneNode(true));

        fromMainArticles = true;
        return wrapper;
    }

    /// <summary>Extracts article title by priority: h1 in container → og:title → &lt;title&gt;.</summary>
    private static string? ExtractTitle(HtmlDocument doc, HtmlNode? container)
    {
        if (container != null)
        {
            var h1 = container.SelectSingleNode(".//h1");
            var h1Text = NormalizeInlineText(h1?.InnerText);
            if (!string.IsNullOrWhiteSpace(h1Text))
            {
                h1?.Remove();
                return h1Text;
            }
        }

        var og = doc.DocumentNode.SelectSingleNode("//meta[@property='og:title' or @name='og:title']");
        var ogText = NormalizeInlineText(og?.GetAttributeValue("content", ""));
        if (!string.IsNullOrWhiteSpace(ogText))
            return ogText;

        var titleNode = doc.DocumentNode.SelectSingleNode("//title");
        var titleText = NormalizeInlineText(titleNode?.InnerText);
        return string.IsNullOrWhiteSpace(titleText) ? null : titleText;
    }

    /// <summary>Remove author/date/share/tags blocks even when inside article container.</summary>
    private static void RemoveArticleNoiseBlocks(HtmlNode root)
    {
        string[] patterns =
        {
            "author", "byline", "dateline", "date", "time", "timestamp",
            "tag", "tags", "category", "breadcrumb",
            "share", "social", "comment", "related", "recommend",
            "subscribe", "newsletter", "promo", "sponsor"
        };

        var toRemove = root.Descendants()
            .Where(n => n.NodeType == HtmlNodeType.Element &&
                        n.Name is "div" or "section" or "aside" or "nav" or "header" or "footer" or
                            "p" or "span" or "time" or "ul" or "ol" or "li")
            .Where(n =>
            {
                var cls = n.GetAttributeValue("class", "").ToLowerInvariant();
                var id = n.GetAttributeValue("id", "").ToLowerInvariant();
                var aria = n.GetAttributeValue("aria-label", "").ToLowerInvariant();
                var role = n.GetAttributeValue("role", "").ToLowerInvariant();
                var itemprop = n.GetAttributeValue("itemprop", "").ToLowerInvariant();
                return patterns.Any(p => cls.Contains(p) || id.Contains(p) || aria.Contains(p) || role.Contains(p) || itemprop.Contains(p));
            })
            .ToList();

        foreach (var n in toRemove)
            n.Remove();
    }

    /// <summary>Remove header/footer/navigation blocks inside &lt;main&gt;.</summary>
    private static void RemoveMainNoiseBlocks(HtmlNode root)
    {
        string[] patterns =
        {
            "header", "footer", "navbar", "topbar", "breadcrumb", "menu",
            "nav", "toolbar", "banner", "masthead"
        };

        var toRemove = root.Descendants()
            .Where(n => n.NodeType == HtmlNodeType.Element &&
                        n != root &&
                        n.Name is "div" or "section" or "nav" or "header" or "footer" or "ul" or "ol" or "li")
            .Where(n =>
            {
                var cls = n.GetAttributeValue("class", "").ToLowerInvariant();
                var id = n.GetAttributeValue("id", "").ToLowerInvariant();
                var aria = n.GetAttributeValue("aria-label", "").ToLowerInvariant();
                var role = n.GetAttributeValue("role", "").ToLowerInvariant();
                return patterns.Any(p => cls.Contains(p) || id.Contains(p) || aria.Contains(p) || role.Contains(p));
            })
            .ToList();

        foreach (var node in toRemove)
        {
            var textLen = NormalizeInlineText(node.InnerText).Length;
            var linkCount = node.SelectNodes(".//a")?.Count ?? 0;
            if (textLen < 400 || linkCount >= 6)
                node.Remove();
        }
    }

    /// <summary>Normalize figure blocks into img + caption paragraphs.</summary>
    private static void NormalizeFigures(HtmlNode container)
    {
        var figures = container.SelectNodes(".//figure")?.ToList() ?? [];
        if (figures.Count == 0) return;

        foreach (var figure in figures)
        {
            var captionNode = figure.SelectSingleNode(".//figcaption");
            var caption = NormalizeInlineText(captionNode?.InnerText);

            var img = figure.SelectSingleNode(".//img") ?? figure.SelectSingleNode(".//picture//img");
            if (img != null)
            {
                img.Remove();
                figure.ParentNode?.InsertBefore(img, figure);
            }

            if (!string.IsNullOrWhiteSpace(caption))
            {
                var p = container.OwnerDocument.CreateElement("p");
                p.InnerHtml = HtmlEntity.Entitize(caption);
                if (img != null)
                    figure.ParentNode?.InsertAfter(p, img);
                else
                    figure.ParentNode?.InsertBefore(p, figure);
            }

            captionNode?.Remove();
            figure.Remove();
        }
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

    private async Task<ImagePrepareSummary> PrepareImagesAsync(HtmlNode container, Uri baseUri, HttpClient client)
    {
        if (container == null) return new ImagePrepareSummary(0, 0, 0);

        int downloaded = 0;
        int skipped = 0;
        int failed = 0;
        var urlToFile = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var pictureNodes = container.SelectNodes(".//picture")?.ToList() ?? [];
        foreach (var picture in pictureNodes)
        {
            var bestSource = GetBestPictureSource(picture);
            if (string.IsNullOrWhiteSpace(bestSource))
            {
                skipped++;
                picture.Remove();
                continue;
            }

            var img = picture.SelectSingleNode(".//img") ?? container.OwnerDocument.CreateElement("img");
            var outcome = await PrepareImageNodeAsync(img, bestSource, baseUri, client, urlToFile);
            downloaded += outcome.Downloaded;
            skipped += outcome.Skipped;
            failed += outcome.Failed;

            img.SetAttributeValue("data-ai-processed", "1");
            img.Remove();
            picture.ParentNode?.InsertBefore(img, picture);
            picture.Remove();
        }

        var imgNodes = container.SelectNodes(".//img")?.ToList() ?? [];
        foreach (var img in imgNodes)
        {
            if (img.GetAttributeValue("data-ai-processed", "") == "1")
            {
                img.Attributes.Remove("data-ai-processed");
                continue;
            }

            var src = GetBestImageSource(img);
            if (string.IsNullOrWhiteSpace(src))
            {
                skipped++;
                img.Remove();
                continue;
            }

            var outcome = await PrepareImageNodeAsync(img, src, baseUri, client, urlToFile);
            downloaded += outcome.Downloaded;
            skipped += outcome.Skipped;
            failed += outcome.Failed;
        }

        return new ImagePrepareSummary(downloaded, skipped, failed);
    }

    private async Task<ImagePrepareSummary> PrepareImageNodeAsync(
        HtmlNode img,
        string source,
        Uri baseUri,
        HttpClient client,
        Dictionary<string, string> urlToFile)
    {
        if (string.IsNullOrWhiteSpace(source))
            return new ImagePrepareSummary(0, 1, 0);

        img.SetAttributeValue("src", source);

        if (TryParseDataUri(source, out var dataMime, out var dataBytes))
        {
            if (dataBytes == null || dataBytes.Length == 0)
                return new ImagePrepareSummary(0, 0, 1);

            var ext = GetExtensionFromMimeType(dataMime) ?? ".png";
            var key = "data:" + Convert.ToHexString(SHA256.HashData(dataBytes));

            if (urlToFile.TryGetValue(key, out var cached))
            {
                img.SetAttributeValue("src", $"{WebAssetsDirName}/{cached}");
                RemoveLazyAttributes(img);
                return new ImagePrepareSummary(0, 0, 0);
            }

            var fileName = BuildFileName(key, ext);
            var tempRoot = EnsureWebAssetsTempRoot(baseUri);
            var tempPath = Path.Combine(tempRoot, fileName);
            await File.WriteAllBytesAsync(tempPath, dataBytes);

            urlToFile[key] = fileName;
            TrackWebAsset(fileName, tempPath);
            img.SetAttributeValue("src", $"{WebAssetsDirName}/{fileName}");
            RemoveLazyAttributes(img);
            return new ImagePrepareSummary(1, 0, 0);
        }

        if (!Uri.TryCreate(baseUri, source, out var resolvedUri))
            return new ImagePrepareSummary(0, 1, 0);

        if (resolvedUri.Scheme != Uri.UriSchemeHttp && resolvedUri.Scheme != Uri.UriSchemeHttps)
            return new ImagePrepareSummary(0, 1, 0);

        var keyUrl = resolvedUri.AbsoluteUri;
        if (urlToFile.TryGetValue(keyUrl, out var existing))
        {
            img.SetAttributeValue("src", $"{WebAssetsDirName}/{existing}");
            RemoveLazyAttributes(img);
            return new ImagePrepareSummary(0, 0, 0);
        }

        try
        {
            using var response = await client.GetAsync(resolvedUri, HttpCompletionOption.ResponseHeadersRead);
            if (!response.IsSuccessStatusCode)
                return new ImagePrepareSummary(0, 0, 1);

            var ext = GetExtensionFromUrl(resolvedUri) ?? GetExtensionFromMimeType(response.Content.Headers.ContentType?.MediaType) ?? ".jpg";
            var fileName = BuildFileName(keyUrl, ext);
            var tempRoot = EnsureWebAssetsTempRoot(baseUri);
            var tempPath = Path.Combine(tempRoot, fileName);

            if (!File.Exists(tempPath))
            {
                await using var input = await response.Content.ReadAsStreamAsync();
                await using var output = File.Create(tempPath);
                await input.CopyToAsync(output);
            }

            urlToFile[keyUrl] = fileName;
            TrackWebAsset(fileName, tempPath);
            img.SetAttributeValue("src", $"{WebAssetsDirName}/{fileName}");
            RemoveLazyAttributes(img);
            return new ImagePrepareSummary(1, 0, 0);
        }
        catch
        {
            return new ImagePrepareSummary(0, 0, 1);
        }
    }

    private static string? GetBestPictureSource(HtmlNode picture)
    {
        string? bestUrl = null;
        double bestScore = -1;

        var sources = picture.SelectNodes(".//source")?.ToList() ?? [];
        foreach (var source in sources)
        {
            var srcset = GetAttributeNonEmpty(source, "data-srcset") ?? GetAttributeNonEmpty(source, "srcset");
            if (!string.IsNullOrWhiteSpace(srcset) && TryPickBestSrcsetCandidate(srcset, out var srcsetUrl, out var score))
            {
                if (score > bestScore)
                {
                    bestScore = score;
                    bestUrl = srcsetUrl;
                }
            }

            var direct = GetAttributeNonEmpty(source, "data-src") ?? GetAttributeNonEmpty(source, "src");
            if (!string.IsNullOrWhiteSpace(direct) && bestScore < 0)
            {
                bestUrl = direct;
                bestScore = 0;
            }
        }

        var img = picture.SelectSingleNode(".//img");
        var imgSrc = img != null ? GetBestImageSource(img) : null;
        if (!string.IsNullOrWhiteSpace(imgSrc) && bestScore < 0)
            return imgSrc;

        return bestUrl;
    }

    private static string? GetBestImageSource(HtmlNode img)
    {
        var srcset = GetAttributeNonEmpty(img, "data-srcset") ?? GetAttributeNonEmpty(img, "srcset");
        if (!string.IsNullOrWhiteSpace(srcset) && TryPickBestSrcsetCandidate(srcset, out var url, out _))
            return url;

        var src = GetAttributeNonEmpty(img, "data-src")
            ?? GetAttributeNonEmpty(img, "data-original")
            ?? GetAttributeNonEmpty(img, "data-lazy-src")
            ?? GetAttributeNonEmpty(img, "data-actualsrc")
            ?? GetAttributeNonEmpty(img, "src");
        return src;
    }

    private static bool TryPickBestSrcsetCandidate(string srcset, out string url, out double score)
    {
        url = "";
        score = -1;

        var parts = srcset.Split(',')
            .Select(p => p.Trim())
            .Where(p => p.Length > 0)
            .ToList();

        foreach (var part in parts)
        {
            var tokens = part.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0) continue;

            var candidateUrl = tokens[0];
            var candidateScore = 0d;

            if (tokens.Length > 1)
            {
                var descriptor = tokens[1].Trim();
                if (descriptor.EndsWith("w", StringComparison.OrdinalIgnoreCase) &&
                    double.TryParse(descriptor.TrimEnd('w', 'W'), out var w))
                    candidateScore = w;
                else if (descriptor.EndsWith("x", StringComparison.OrdinalIgnoreCase) &&
                         double.TryParse(descriptor.TrimEnd('x', 'X'), out var x))
                    candidateScore = x * 1000;
            }

            if (candidateScore >= score)
            {
                score = candidateScore;
                url = candidateUrl;
            }
        }

        return !string.IsNullOrWhiteSpace(url);
    }

    private static string? GetAttributeNonEmpty(HtmlNode node, string name)
    {
        var value = node.GetAttributeValue(name, "").Trim();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static void RemoveLazyAttributes(HtmlNode img)
    {
        img.Attributes.Remove("srcset");
        img.Attributes.Remove("data-srcset");
        img.Attributes.Remove("data-src");
        img.Attributes.Remove("data-original");
        img.Attributes.Remove("data-lazy-src");
        img.Attributes.Remove("data-actualsrc");
    }

    private static string? GetExtensionFromUrl(Uri uri)
    {
        var ext = Path.GetExtension(uri.AbsolutePath);
        if (string.IsNullOrWhiteSpace(ext)) return null;
        return ext.StartsWith(".") ? ext.ToLowerInvariant() : "." + ext.ToLowerInvariant();
    }

    private static string? GetExtensionFromMimeType(string? mimeType)
    {
        if (string.IsNullOrWhiteSpace(mimeType)) return null;
        return mimeType.ToLowerInvariant() switch
        {
            "image/png" => ".png",
            "image/jpeg" => ".jpg",
            "image/jpg" => ".jpg",
            "image/gif" => ".gif",
            "image/webp" => ".webp",
            "image/svg+xml" => ".svg",
            "image/bmp" => ".bmp",
            "image/tiff" => ".tif",
            _ => null
        };
    }

    private static bool TryParseDataUri(string source, out string? mimeType, out byte[]? dataBytes)
    {
        mimeType = null;
        dataBytes = null;

        if (!source.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return false;

        var commaIdx = source.IndexOf(',');
        if (commaIdx < 0) return false;

        var header = source.Substring(5, commaIdx - 5);
        var dataPart = source.Substring(commaIdx + 1);

        mimeType = header.Split(';').FirstOrDefault();
        var isBase64 = header.Contains(";base64", StringComparison.OrdinalIgnoreCase);

        try
        {
            dataBytes = isBase64
                ? Convert.FromBase64String(dataPart)
                : Encoding.UTF8.GetBytes(Uri.UnescapeDataString(dataPart));
            return true;
        }
        catch
        {
            dataBytes = null;
            return true;
        }
    }

    private void TrackWebAsset(string fileName, string tempPath)
    {
        if (_webAssets.Any(a => a.FileName.Equals(fileName, StringComparison.OrdinalIgnoreCase)))
            return;
        _webAssets.Add(new WebAssetFile(fileName, tempPath));
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
        "script", "style", "noscript", "iframe", "button", "input",
        "select", "textarea", "video", "audio", "canvas", "map",
        "object", "embed", "track"
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

            if (tag == "img")
            {
                AppendImageMarkdown(node, sb);
                return;
            }

            if (tag == "figure")
            {
                AppendFigureMarkdown(node, sb);
                return;
            }

            if (tag == "svg")
                return;

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

    private static void AppendImageMarkdown(HtmlNode img, StringBuilder sb)
    {
        var src = img.GetAttributeValue("src", "").Trim();
        if (string.IsNullOrWhiteSpace(src)) return;

        var alt = NormalizeInlineText(img.GetAttributeValue("alt", ""));
        if (string.IsNullOrWhiteSpace(alt)) alt = "image";

        sb.AppendLine().AppendLine();
        sb.Append($"![{EscapeMarkdownAlt(alt)}]({src})");
        sb.AppendLine().AppendLine();
    }

    private static void AppendFigureMarkdown(HtmlNode figure, StringBuilder sb)
    {
        var img = figure.SelectSingleNode(".//img") ?? figure.SelectSingleNode(".//picture//img");
        if (img != null)
            AppendImageMarkdown(img, sb);

        var caption = NormalizeInlineText(figure.SelectSingleNode(".//figcaption")?.InnerText);
        if (!string.IsNullOrWhiteSpace(caption))
        {
            sb.AppendLine(caption);
            sb.AppendLine();
        }
    }

    private static string EscapeMarkdownAlt(string text)
    {
        return text.Replace("[", "\\[").Replace("]", "\\]");
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
        IsSpeechPaused = false;
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
            IsSpeechPaused = false;
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
        IsSpeechPaused = false;
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
            IsSpeechPaused = false;
            _speechCts?.Dispose();
            _speechCts = null;
        }
    }

    [RelayCommand]
    private async Task PauseSpeechAsync()
    {
        if (!IsSpeaking || IsSpeechPaused) return;
        await _speechService.PauseAsync();
        IsSpeechPaused = true;
        StatusText = "Reading paused.";
    }

    [RelayCommand]
    private async Task ResumeSpeechAsync()
    {
        if (!IsSpeaking || !IsSpeechPaused) return;
        await _speechService.ResumeAsync();
        IsSpeechPaused = false;
        StatusText = "Reading resumed.";
    }

    [RelayCommand]
    private void StopSpeech()
    {
        _speechCts?.Cancel();
        _speechService.Stop();
        IsSpeechPaused = false;
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
                    progressReporter, OnEntryTranslated, _cts.Token, settings.DelayBetweenRequestsMs);

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
