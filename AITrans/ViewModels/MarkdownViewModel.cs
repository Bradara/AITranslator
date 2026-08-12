using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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

    [ObservableProperty]
    private string _inputText = "";

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
    private string _translationProviderMode = "С ИИ";

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

    /// <summary>1-based row number currently active (last clicked/scrolled-to), shown next to the "go to row" navigator.</summary>
    [ObservableProperty]
    private int _currentRowNumber = 1;

    /// <summary>Bound to the "go to row" navigator input.</summary>
    [ObservableProperty]
    private int _goToRowNumber = 1;

    /// <summary>Bound to the search box; searches original and translated text.</summary>
    [ObservableProperty]
    private string _searchText = "";

    /// <summary>Shows the current match position, e.g. "3 / 7", or "No matches".</summary>
    [ObservableProperty]
    private string _searchStatusText = "";

    private List<int> _searchMatchIndices = [];
    private int _currentSearchMatchPos = -1;

    private List<int> _selectedIndices = [];
    private int _lastTranslatedIndex = -1;
    private int _lastSelectedIndex = -1;

    public string[] AvailableLanguages { get; } = ["Bulgarian", "Russian", "English"];

    public string[] AvailableTranslationProviders { get; } = ["С ИИ", "DeepL", "Azure", "Google"];

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
        TranslationProviderMode = ResolveTranslationProviderMode(settingsService.Settings);
        UpdateCacheInfo();
    }

    private static string ResolveTranslationProviderMode(AppSettings s) => s switch
    {
        { UseAzureTranslatorForMarkdown: true } => "Azure",
        { UseDeepLForMarkdown: true } => "DeepL",
        { UseGoogleTranslateForMarkdown: true } => "Google",
        _ => "С ИИ"
    };

    partial void OnTranslationProviderModeChanged(string value)
    {
        var s = _settingsService.Settings;
        s.UseAzureTranslatorForMarkdown = value == "Azure";
        s.UseDeepLForMarkdown = value == "DeepL";
        s.UseGoogleTranslateForMarkdown = value == "Google";
        _settingsService.Save();
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

    /// <summary>Called by the view whenever the topmost visible row changes due to scrolling
    /// (mouse wheel, scrollbar drag, keyboard) so the "active row" is tracked even without a click.</summary>
    public void NotifyActiveRow(int idx) => UpdateLastSelectedIndex(idx);

    [RelayCommand]
    private void GoToRow()
    {
        if (Paragraphs.Count == 0) return;
        var idx = Math.Clamp(GoToRowNumber - 1, 0, Paragraphs.Count - 1);
        UpdateLastSelectedIndex(idx);
        ScrollToRow = idx;
    }

    // ── Search: highlights matching rows and steps through them ─────────────

    partial void OnSearchTextChanged(string value) => PerformSearch();

    private void PerformSearch()
    {
        foreach (var p in Paragraphs)
        {
            p.IsSearchMatch = false;
            p.IsCurrentSearchMatch = false;
        }

        var term = SearchText.Trim();
        if (term.Length == 0)
        {
            _searchMatchIndices = [];
            _currentSearchMatchPos = -1;
            SearchStatusText = "";
            return;
        }

        _searchMatchIndices = Paragraphs
            .Select((p, i) => (p, i))
            .Where(t => Contains(t.p.OriginalText, term) || Contains(t.p.TranslatedText, term))
            .Select(t => t.i)
            .ToList();

        foreach (var idx in _searchMatchIndices)
            Paragraphs[idx].IsSearchMatch = true;

        if (_searchMatchIndices.Count == 0)
        {
            _currentSearchMatchPos = -1;
            SearchStatusText = "No matches";
            return;
        }

        _currentSearchMatchPos = 0;
        GoToCurrentSearchMatch();
    }

    private static bool Contains(string? text, string term)
        => !string.IsNullOrEmpty(text) && text.Contains(term, StringComparison.OrdinalIgnoreCase);

    [RelayCommand]
    private void FindNext()
    {
        if (_searchMatchIndices.Count == 0) return;
        _currentSearchMatchPos = (_currentSearchMatchPos + 1) % _searchMatchIndices.Count;
        GoToCurrentSearchMatch();
    }

    [RelayCommand]
    private void FindPrevious()
    {
        if (_searchMatchIndices.Count == 0) return;
        _currentSearchMatchPos = (_currentSearchMatchPos - 1 + _searchMatchIndices.Count) % _searchMatchIndices.Count;
        GoToCurrentSearchMatch();
    }

    private void GoToCurrentSearchMatch()
    {
        foreach (var idx in _searchMatchIndices)
            Paragraphs[idx].IsCurrentSearchMatch = false;

        var rowIndex = _searchMatchIndices[_currentSearchMatchPos];
        Paragraphs[rowIndex].IsCurrentSearchMatch = true;
        SearchStatusText = $"{_currentSearchMatchPos + 1} / {_searchMatchIndices.Count}";
        ScrollToRow = rowIndex;
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
        PerformSearch();

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
        RequestRestoreScroll();
        StatusText = $"Loaded {Paragraphs.Count} paragraphs from {Path.GetFileName(path)}";
    }

    public void SaveTranslation(string path)
    {
        var text = GetCombinedTranslation();
        File.WriteAllText(path, text);
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
        PersistSessionState();
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
        RequestRestoreScroll();
        PerformSearch();
    }

    public void RefreshCacheInfo() => UpdateCacheInfo();

    /// <summary>
    /// Restores the last active row for this session: syncs the "go to row" navigator and signals
    /// the view to scroll to it. Used when a cached session loads and when the tab regains visibility.
    /// </summary>
    public void RequestRestoreScroll()
    {
        if (Paragraphs.Count == 0) return;
        var idx = GetRestoreRowIndex();
        GoToRowNumber = idx + 1;
        ScrollToRow = idx;
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
        RequestRestoreScroll();
        PerformSearch();
        StatusText = $"Parsed {parts.Count} paragraphs.";
        OnPropertyChanged(nameof(HasParagraphs));
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

            // Paragraphs that are nothing but a link/image (e.g. a standalone ![](images/x.jpg))
            // have no translatable content — copy them through untouched instead of risking a
            // provider mangling the URL or a placeholder round-trip.
            var linkOnly = indexMap.Where(i => MarkdownLinkProtector.IsLinkOnly(Paragraphs[i].OriginalText)).ToList();
            foreach (var i in linkOnly)
            {
                Paragraphs[i].TranslatedText = Paragraphs[i].OriginalText;
                SetLastTranslatedIndex(i);
                translated++;
                StatusText = $"Translated {translated}/{total}...";
            }
            indexMap = indexMap.Except(linkOnly).ToList();

            if (indexMap.Count == 0)
            {
                StatusText = $"Done. {translated} of {total} paragraphs translated.";
                return;
            }

            // Shield markdown link/image URLs (e.g. ../images/00001.jpeg) from translation —
            // otherwise providers translate path segments along with the prose and break the link.
            var linkMaps = new List<string>[indexMap.Count];
            var texts = indexMap.Select((i, batchIdx) =>
            {
                var (protectedText, urls) = MarkdownLinkProtector.Protect(Paragraphs[i].OriginalText);
                linkMaps[batchIdx] = urls;
                return protectedText;
            }).ToList();

            var progressReporter = new Progress<int>(p => Progress = p);

            void OnEntryTranslated(int batchIdx, string text)
            {
                if (batchIdx >= 0 && batchIdx < indexMap.Count)
                {
                    var realIdx = indexMap[batchIdx];
                    Paragraphs[realIdx].TranslatedText = MarkdownLinkProtector.Restore(text, linkMaps[batchIdx]);
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
                        Paragraphs[realIdx].TranslatedText = MarkdownLinkProtector.Restore(translations[i], linkMaps[i]);
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
                        Paragraphs[realIdx].TranslatedText = MarkdownLinkProtector.Restore(translations[i], linkMaps[i]);
                }
            }
            else if (settings.UseGoogleTranslateForMarkdown)
            {
                var translations = await _translationService.TranslateGoogleFreeBatchAsync(
                    texts, SelectedLanguage,
                    progressReporter, OnEntryTranslated, _cts.Token, settings.DelayBetweenRequestsMs);

                for (int i = 0; i < indexMap.Count && i < translations.Count; i++)
                {
                    var realIdx = indexMap[i];
                    if (!string.IsNullOrEmpty(translations[i]) && string.IsNullOrEmpty(Paragraphs[realIdx].TranslatedText))
                        Paragraphs[realIdx].TranslatedText = MarkdownLinkProtector.Restore(translations[i], linkMaps[i]);
                }
            }
            else
            {
                var apiKey = settings.ActiveApiKey;
                if (string.IsNullOrWhiteSpace(apiKey) && settings.ActiveProviderRequiresApiKey)
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
                        Paragraphs[realIdx].TranslatedText = MarkdownLinkProtector.Restore(translations[i], linkMaps[i]);
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
        CurrentRowNumber = idx + 1;
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
            var clamped = Math.Clamp(selectedIdx, 0, Paragraphs.Count - 1);
            CurrentRowNumber = clamped + 1;
            return clamped;
        }
        if (!_settingsService.Settings.MarkdownLastTranslatedIndexByFile.TryGetValue(key, out var lastIdx))
            lastIdx = GetLastTranslatedIndexFromParagraphs();
        _lastTranslatedIndex = lastIdx;
        if (lastIdx >= 0 && !string.IsNullOrWhiteSpace(key))
            _settingsService.Settings.MarkdownLastTranslatedIndexByFile[key] = lastIdx;
        var result = lastIdx < 0 ? 0 : Math.Min(lastIdx + 1, Paragraphs.Count - 1);
        CurrentRowNumber = result + 1;
        return result;
    }
}
