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
    }

    public void LoadFile(string path)
    {
        var content = File.ReadAllText(path);
        InputText = content;
        LoadedFilePath = path;
        ParseParagraphs();
        ScrollToRow = 0;
        StatusText = $"Loaded {Paragraphs.Count} paragraphs from {Path.GetFileName(path)}";
    }

    public void SaveTranslation(string path)
    {
        File.WriteAllText(path, GetCombinedTranslation());
        StatusText = $"Saved to {Path.GetFileName(path)}";
        // Cache is preserved so translation progress isn't lost
        UpdateCacheInfo();
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
        ScrollToRow = Paragraphs
            .Select((p, i) => (p, i))
            .FirstOrDefault(x => string.IsNullOrEmpty(x.p.TranslatedText)).i;
    }

    public void RefreshCacheInfo() => UpdateCacheInfo();

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

        // Split on double newlines (blank lines) to get paragraphs
        var parts = InputText
            .Replace("\r\n", "\n")
            .Split(["\n\n"], StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Trim())
            .Where(p => p.Length > 0)
            .ToList();

        var entries = new ObservableCollection<MarkdownEntry>();
        for (int i = 0; i < parts.Count; i++)
        {
            entries.Add(new MarkdownEntry { Index = i + 1, OriginalText = parts[i] });
        }

        Paragraphs = entries;
        ScrollToRow = 0;
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
}
