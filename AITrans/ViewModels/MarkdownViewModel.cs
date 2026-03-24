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
    private CancellationTokenSource? _cts;

    [ObservableProperty]
    private string _inputText = "";

    [ObservableProperty]
    private ObservableCollection<MarkdownEntry> _paragraphs = [];

    [ObservableProperty]
    private bool _isTranslating;

    [ObservableProperty]
    private int _progress;

    [ObservableProperty]
    private string _selectedLanguage = "Bulgarian";

    [ObservableProperty]
    private string _statusText = "Ready";

    [ObservableProperty]
    private string? _loadedFilePath;

    private List<int> _selectedIndices = [];

    public string[] AvailableLanguages { get; } = ["Bulgarian", "Russian", "English"];

    public bool HasParagraphs => Paragraphs.Count > 0;

    public MarkdownViewModel(TranslationService translationService, SettingsService settingsService)
    {
        _translationService = translationService;
        _settingsService = settingsService;
        SelectedLanguage = settingsService.Settings.DefaultLanguage;
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
        StatusText = $"Loaded {Paragraphs.Count} paragraphs from {Path.GetFileName(path)}";
    }

    public void SaveTranslation(string path)
    {
        File.WriteAllText(path, GetCombinedTranslation());
        StatusText = $"Saved to {Path.GetFileName(path)}";
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

            // Use small batch size for markdown paragraphs (max 5)
            var batchSize = Math.Min(settings.BatchSize, 5);

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
