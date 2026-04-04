using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AITrans.Models;
using AITrans.Services;

namespace AITrans.ViewModels;

public partial class SubtitlesViewModel : ViewModelBase
{
    private readonly TranslationService _translationService;
    private readonly SettingsService _settingsService;
    private readonly CacheService _cacheService;
    private CancellationTokenSource? _cts;

    [ObservableProperty]
    private ObservableCollection<SrtEntry> _entries = [];

    [ObservableProperty]
    private bool _isTranslating;

    [ObservableProperty]
    private int _progress;

    [ObservableProperty]
    private string _selectedLanguage = "Bulgarian";

    [ObservableProperty]
    private string? _loadedFilePath;

    [ObservableProperty]
    private string _statusText = "Ready";

    [ObservableProperty]
    private string _cacheInfo = "";

    [ObservableProperty]
    private bool _hasCache;

    /// <summary>Set by commands to signal the view to scroll to a specific row index. View resets to -1 after handling.</summary>
    [ObservableProperty]
    private int _scrollToRow = -1;

    // Indices selected in the DataGrid
    private List<int> _selectedIndices = [];
    private int _lastTranslatedIndex = -1;
    private int _lastSelectedIndex = -1;

    public string[] AvailableLanguages { get; } = ["Bulgarian", "Russian", "English"];

    public bool HasEntries => Entries.Count > 0;

    internal CacheService CacheService => _cacheService;

    public SubtitlesViewModel(TranslationService translationService, SettingsService settingsService, CacheService cacheService)
    {
        _translationService = translationService;
        _settingsService = settingsService;
        _cacheService = cacheService;
        SelectedLanguage = settingsService.Settings.DefaultLanguage;
        UpdateCacheInfo(null);
    }

    public void SetSelectedIndices(List<int> indices)
    {
        _selectedIndices = indices;
        if (_selectedIndices.Count > 0)
            UpdateLastSelectedIndex(_selectedIndices.Min());
    }

    public void LoadFile(string path)
    {
        var parsed = SrtParser.Parse(path);
        Entries = new ObservableCollection<SrtEntry>(parsed);
        LoadedFilePath = path;
        StatusText = $"Loaded {parsed.Count} entries from {System.IO.Path.GetFileName(path)}";
        OnPropertyChanged(nameof(HasEntries));
        UpdateCacheInfo(path);
        ScrollToRow = GetRestoreRowIndex();
    }

    public void SaveFile(string path)
    {
        SrtParser.Write(path, [.. Entries]);
        StatusText = $"Saved to {System.IO.Path.GetFileName(path)}";
        // Cache is preserved so translation progress isn't lost
        UpdateCacheInfo(LoadedFilePath);
    }

    [RelayCommand]
    private void SaveCache()
    {
        if (Entries.Count == 0) { StatusText = "Nothing to cache."; return; }
        var filePath = LoadedFilePath ?? "unsaved";
        _cacheService.SaveSubtitleSession(filePath, SelectedLanguage, Entries);
        UpdateCacheInfo(filePath);
        StatusText = $"Session cached ({Entries.Count} entries).";
    }

    [RelayCommand]
    private void LoadCache()
    {
        string? keyToLoad = null;
        if (LoadedFilePath != null && _cacheService.GetSubtitleCacheInfo(LoadedFilePath) != null)
            keyToLoad = LoadedFilePath;
        else
            keyToLoad = _cacheService.GetLatestSubtitleSession()?.FilePath;

        if (keyToLoad == null) { StatusText = "No cached session found."; return; }
        LoadCacheFromKey(keyToLoad);
    }

    public void LoadCacheFromKey(string key)
    {
        var entries = _cacheService.LoadSubtitleEntries(key);
        if (entries == null || entries.Count == 0) { StatusText = "Cached session is empty."; return; }

        if (LoadedFilePath == null) LoadedFilePath = key;
        Entries = new ObservableCollection<SrtEntry>(entries);
        OnPropertyChanged(nameof(HasEntries));
        var info = _cacheService.GetSubtitleCacheInfo(key);
        StatusText = $"Restored {entries.Count} entries from cache ({info?.TranslatedEntries}/{info?.TotalEntries} translated).";
        UpdateCacheInfo(key);
        ScrollToRow = GetRestoreRowIndex();
    }

    public void RefreshCacheInfo() => UpdateCacheInfo(LoadedFilePath);

    public void RequestRestoreScroll()
    {
        if (Entries.Count == 0) return;
        ScrollToRow = GetRestoreRowIndex();
    }

    private void UpdateCacheInfo(string? filePath)
    {
        var all = _cacheService.GetAllSubtitleSessions();
        if (all.Count == 0)
        {
            HasCache = false;
            CacheInfo = "";
            return;
        }
        HasCache = true;
        var info = (filePath != null ? all.Find(s => s.FilePath == filePath) : null) ?? all[0];
        var name = System.IO.Path.GetFileName(info.FilePath);
        if (string.IsNullOrEmpty(name)) name = info.FilePath;
        CacheInfo = all.Count == 1
            ? $"Cached: {name} — {info.TranslatedEntries}/{info.TotalEntries} translated — {info.SavedAt.ToLocalTime():HH:mm}"
            : $"Cached: {all.Count} sessions (latest: {name} — {info.SavedAt.ToLocalTime():HH:mm})";
    }

    [RelayCommand]
    private async Task TranslateAsync()
    {
        await TranslateRangeAsync(0, Entries.Count - 1);
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
        await TranslateRangeAsync(fromIndex, Entries.Count - 1);
    }

    [RelayCommand]
    private void CancelTranslation()
    {
        _cts?.Cancel();
    }

    private async Task TranslateRangeAsync(int from, int to)
    {
        var indices = Enumerable.Range(from, to - from + 1).ToList();
        await TranslateIndicesAsync(indices);
    }

    private async Task TranslateIndicesAsync(List<int> indices)
    {
        if (Entries.Count == 0 || indices.Count == 0) return;

        var settings = _settingsService.Settings;
        var apiKey = settings.ActiveApiKey;
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            StatusText = "Error: API key not set. Go to Settings tab.";
            return;
        }

        IsTranslating = true;
        Progress = 0;
        _cts = new CancellationTokenSource();

        int translated = 0;
        var total = indices.Count;
        StatusText = $"Translating {total} entries...";

        try
        {
            // Build a mapping: position in batch list -> actual entry index
            var indexMap = indices.Where(i => i >= 0 && i < Entries.Count).ToList();
            var texts = indexMap.Select(i => Entries[i].OriginalText).ToList();

            var progressReporter = new Progress<int>(p => Progress = p);

            void OnEntryTranslated(int batchIdx, string text)
            {
                if (batchIdx >= 0 && batchIdx < indexMap.Count)
                {
                    var realIdx = indexMap[batchIdx];
                    Entries[realIdx].TranslatedText = text;
                    SetLastTranslatedIndex(realIdx);
                    translated++;
                    StatusText = $"Translated {translated}/{total}...";
                }
            }

            // Use batch indices 0..N, then map back
            var translations = await _translationService.TranslateSubtitleBatchAsync(
                texts, SelectedLanguage, apiKey, settings.ActiveModel, settings.ActiveEndpoint,
                settings.BatchSize, settings.DelayBetweenRequestsMs,
                progressReporter, OnEntryTranslated, settings, _cts.Token);

            // Fill in any the callback might have missed
            for (int i = 0; i < indexMap.Count && i < translations.Count; i++)
            {
                var realIdx = indexMap[i];
                if (!string.IsNullOrEmpty(translations[i]) && string.IsNullOrEmpty(Entries[realIdx].TranslatedText))
                    Entries[realIdx].TranslatedText = translations[i];
            }

            StatusText = $"Done. {translated} of {total} entries translated.";
        }
        catch (OperationCanceledException)
        {
            StatusText = $"Cancelled. {translated} of {total} entries translated.";
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
        if (Entries.Count == 0) return;
        var key = GetSessionKey();
        var lastIdx = GetLastTranslatedIndexFromEntries();
        if (!string.IsNullOrWhiteSpace(key) && lastIdx >= 0)
            _settingsService.Settings.SubtitlesLastTranslatedIndexByFile[key] = lastIdx;
        if (!string.IsNullOrWhiteSpace(key) && _lastSelectedIndex >= 0)
            _settingsService.Settings.SubtitlesLastSelectedIndexByFile[key] = _lastSelectedIndex;
        _settingsService.Save();
    }

    private string GetSessionKey() => LoadedFilePath ?? "unsaved";

    private int GetLastTranslatedIndexFromEntries()
    {
        for (int i = Entries.Count - 1; i >= 0; i--)
        {
            if (!string.IsNullOrEmpty(Entries[i].TranslatedText))
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
            _settingsService.Settings.SubtitlesLastTranslatedIndexByFile[key] = _lastTranslatedIndex;
    }

    private void UpdateLastSelectedIndex(int idx)
    {
        if (idx < 0) return;
        _lastSelectedIndex = idx;
        var key = GetSessionKey();
        if (!string.IsNullOrWhiteSpace(key))
            _settingsService.Settings.SubtitlesLastSelectedIndexByFile[key] = _lastSelectedIndex;
    }

    private int GetRestoreRowIndex()
    {
        if (Entries.Count == 0) return -1;
        var key = GetSessionKey();
        if (_settingsService.Settings.SubtitlesLastSelectedIndexByFile.TryGetValue(key, out var selectedIdx)
            && selectedIdx >= 0)
        {
            _lastSelectedIndex = selectedIdx;
            return Math.Clamp(selectedIdx, 0, Entries.Count - 1);
        }
        if (!_settingsService.Settings.SubtitlesLastTranslatedIndexByFile.TryGetValue(key, out var lastIdx))
            lastIdx = GetLastTranslatedIndexFromEntries();
        _lastTranslatedIndex = lastIdx;
        if (lastIdx >= 0 && !string.IsNullOrWhiteSpace(key))
            _settingsService.Settings.SubtitlesLastTranslatedIndexByFile[key] = lastIdx;
        if (lastIdx < 0) return 0;
        return Math.Min(lastIdx + 1, Entries.Count - 1);
    }
}
