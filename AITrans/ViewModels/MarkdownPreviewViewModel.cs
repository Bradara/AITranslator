using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AITrans.Services;

namespace AITrans.ViewModels;

public partial class MarkdownPreviewViewModel : ViewModelBase
{
    private readonly SpeechService _speechService;
    private readonly SettingsService _settingsService;
    private readonly CacheService _cacheService;
    private readonly EpubExportService _epubExportService;
    private CancellationTokenSource? _speechCts;
    private readonly Stack<string> _navHistory = new();
    private bool _loadingFile;

    internal CacheService CacheService => _cacheService;

    // List of (charStart in PlainText, plain paragraph text)
    private List<(int charStart, string text)> _paragraphSpans = [];
    private int _selectionStart;
    private double _lastScrollRatio;

    [ObservableProperty]
    private string _markdownText = "";

    [ObservableProperty]
    private string _plainText = "";

    [ObservableProperty]
    private int _scrollToParagraph = -1;

    [ObservableProperty]
    private bool _isSpeaking;

    [ObservableProperty]
    private bool _isSpeechPaused;

    [ObservableProperty]
    private bool _isExporting;

    [ObservableProperty]
    private string _statusText = "Ready — open a markdown file or paste text below";

    [ObservableProperty]
    private string? _loadedFilePath;

    [ObservableProperty]
    private string _readLanguage = "English";

    [ObservableProperty]
    private double _previewFontSize = 18;

    [ObservableProperty]
    private bool _hasUnsavedChanges;

    public string[] AvailableLanguages { get; } = ["Bulgarian", "Russian", "English", "German", "French", "Spanish"];

    public MarkdownPreviewViewModel(
        SpeechService speechService,
        SettingsService settingsService,
        CacheService cacheService,
        EpubExportService epubExportService)
    {
        _speechService = speechService;
        _settingsService = settingsService;
        _cacheService = cacheService;
        _epubExportService = epubExportService;

        // Pre-populate language from settings if set
        var src = settingsService.Settings.SpeechSourceLanguage;
        if (!string.IsNullOrWhiteSpace(src) && AvailableLanguages.Contains(src))
            ReadLanguage = src;
    }

    public async Task<EpubExportResult?> ExportToEpubAsync(
        string outputPath,
        IReadOnlyList<string>? extraBaseDirs = null)
    {
        if (string.IsNullOrWhiteSpace(MarkdownText))
        {
            StatusText = "Nothing to export.";
            return null;
        }

        try
        {
            IsExporting = true;
            StatusText = "Exporting EPUB...";
            var fallbackDirs = new List<string>();
            var workingFolder = _settingsService.Settings.EbookWorkingFolder;
            if (!string.IsNullOrWhiteSpace(workingFolder))
                fallbackDirs.Add(workingFolder);
            if (extraBaseDirs != null)
                fallbackDirs.AddRange(extraBaseDirs.Where(d => !string.IsNullOrWhiteSpace(d)));

            var result = await _epubExportService.ExportAsync(
                MarkdownText,
                LoadedFilePath,
                outputPath,
                ReadLanguage,
                CancellationToken.None,
                fallbackBaseDirs: fallbackDirs);

            if (result.SkippedImages > 0)
            {
                StatusText = $"EPUB exported with {result.SkippedImages} skipped images: {Path.GetFileName(outputPath)}.";
            }
            else
            {
                StatusText = $"EPUB exported: {Path.GetFileName(outputPath)}.";
            }

            return result;
        }
        catch (Exception ex)
        {
            StatusText = $"Export failed: {ex.Message}";
            return null;
        }
        finally
        {
            IsExporting = false;
        }
    }

    /// <summary>Called from code-behind when InputTextBox text changes (paste).</summary>
    public void SetMarkdown(string markdown)
    {
        MarkdownText = markdown;
        StatusText = $"Loaded {_paragraphSpans.Count} paragraphs.";
    }

    partial void OnMarkdownTextChanged(string value)
    {
        if (!_loadingFile)
            HasUnsavedChanges = true;
        BuildPlainText();
    }

    public void LoadFile(string path)
    {
        if (!string.IsNullOrEmpty(LoadedFilePath))
            _navHistory.Push(LoadedFilePath);
        LoadFileCore(path);
        RegisterRecentFile(path);
        GoBackCommand.NotifyCanExecuteChanged();
    }

    private void LoadFileCore(string path)
    {
        _loadingFile = true;
        MarkdownText = File.ReadAllText(path);  // triggers OnMarkdownTextChanged → BuildPlainText()
        LoadedFilePath = path;
        _loadingFile = false;
        HasUnsavedChanges = false;
        StatusText = $"Loaded {_paragraphSpans.Count} paragraphs from {Path.GetFileName(path)}.";
        RestoreLastReadParagraph();
    }

    public void PersistSessionState()
    {
        _settingsService.Settings.LastPreviewFilePath = LoadedFilePath ?? "";
        _settingsService.Save();
    }

    public void RequestRestoreScroll()
    {
        RestoreLastReadParagraph();
    }

    public bool TryGetSavedScrollY(out double y)
    {
        y = 0;
        var key = GetPreviewKey();
        if (string.IsNullOrWhiteSpace(key)) return false;
        if (_settingsService.Settings.PreviewLastScrollOffsetByFile.TryGetValue(key, out var saved) && saved > 0)
        {
            y = saved;
            return true;
        }
        return false;
    }

    public void UpdatePreviewScrollY(double y)
    {
        if (double.IsNaN(y) || double.IsInfinity(y) || y < 0) return;
        _lastScrollRatio = y; // reuse field for in-session dirty tracking
        var key = GetPreviewKey();
        if (!string.IsNullOrWhiteSpace(key))
            _settingsService.Settings.PreviewLastScrollOffsetByFile[key] = y;
    }

    public void UpdateLastReadParagraphFromScrollRatio(double ratio)
    {
        if (_paragraphSpans.Count == 0) return;
        var clamped = Math.Clamp(ratio, 0, 1);
        var idx = (int)Math.Round(clamped * (_paragraphSpans.Count - 1));
        SaveLastReadParagraph(idx);
    }

    private void RegisterRecentFile(string path)
    {
        _cacheService.UpsertPreviewFileHistory(Path.GetFullPath(path));
    }

    /// <summary>Navigates to a URL — local .md files are loaded in the viewer, web URLs open in the browser.</summary>
    public void NavigateTo(string url)
    {
        if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch { /* ignore */ }
            return;
        }

        string resolved;
        if (Path.IsPathRooted(url))
        {
            resolved = url;
        }
        else if (!string.IsNullOrEmpty(LoadedFilePath))
        {
            var dir = Path.GetDirectoryName(LoadedFilePath)!;
            resolved = Path.GetFullPath(Path.Combine(dir, url));
        }
        else
        {
            resolved = Path.GetFullPath(url);
        }

        if (!File.Exists(resolved))
        {
            StatusText = $"File not found: {resolved}";
            return;
        }

        LoadFile(resolved);
    }

    private bool CanGoBack() => _navHistory.Count > 0;

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private void GoBack()
    {
        var prevPath = _navHistory.Pop();
        LoadFileCore(prevPath);
        RegisterRecentFile(prevPath);
        GoBackCommand.NotifyCanExecuteChanged();
    }

    public void SaveToFile(string path)
    {
        File.WriteAllText(path, MarkdownText);
        LoadedFilePath = path;
        RegisterRecentFile(path);
        HasUnsavedChanges = false;
        StatusText = $"Saved {Path.GetFileName(path)}.";
    }

    /// <summary>Called from code-behind when the user moves the caret in the raw editor.</summary>
    public void SetSelectionStart(int charPos)
    {
        _selectionStart = charPos;
        SaveLastReadParagraph(GetParagraphIndexFromChar(charPos));
    }

    // ──────────────────────────────────────────────────────
    //  Commands
    // ──────────────────────────────────────────────────────

    [RelayCommand]
    private async Task ReadAllAsync() => await SpeakFromIndexAsync(0);

    [RelayCommand]
    private async Task ReadFromSelectionAsync()
    {
        var idx = GetParagraphIndexFromChar(_selectionStart);
        await SpeakFromIndexAsync(idx);
    }

    [RelayCommand]
    private void StopSpeech()
    {
        _speechCts?.Cancel();
        _speechService.Stop();
        IsSpeechPaused = false;
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
    private void IncreaseFontSize()
    {
        if (PreviewFontSize < 40) PreviewFontSize += 2;
    }

    [RelayCommand]
    private void DecreaseFontSize()
    {
        if (PreviewFontSize > 8) PreviewFontSize -= 2;
    }

    // ──────────────────────────────────────────────────────
    //  Private helpers
    // ──────────────────────────────────────────────────────

    private void BuildPlainText()
    {
        var raw = MarkdownText.Replace("\r\n", "\n");

        // Split on blank lines (paragraph boundaries)
        var parts = raw.Split(["\n\n"], StringSplitOptions.RemoveEmptyEntries)
            .Select(p => StripMarkdown(p.Trim()))
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .ToList();

        _paragraphSpans = [];
        var sb = new StringBuilder();
        foreach (var p in parts)
        {
            _paragraphSpans.Add((sb.Length, p));
            sb.AppendLine(p);
            sb.AppendLine(); // blank separator so user sees paragraph breaks
        }
        PlainText = sb.ToString();
    }

    private static string StripMarkdown(string text)
    {
        // Remove heading markers (# Header)
        text = Regex.Replace(text, @"^#{1,6}\s+", "", RegexOptions.Multiline);
        // Remove **bold** and *italic* and ***both***
        text = Regex.Replace(text, @"\*{1,3}(.+?)\*{1,3}", "$1", RegexOptions.Singleline);
        // Remove __bold__ and _italic_
        text = Regex.Replace(text, @"_{1,3}(.+?)_{1,3}", "$1", RegexOptions.Singleline);
        // Remove inline code `code`
        text = Regex.Replace(text, @"`(.+?)`", "$1", RegexOptions.Singleline);
        // Remove links [text](url) → text
        text = Regex.Replace(text, @"\[(.+?)\]\([^)]*\)", "$1");
        // Remove images ![alt](url)
        text = Regex.Replace(text, @"!\[.*?\]\([^)]*\)", "");
        // Remove horizontal rules
        text = Regex.Replace(text, @"^[-*_]{3,}\s*$", "", RegexOptions.Multiline);
        // Remove unordered list markers
        text = Regex.Replace(text, @"^\s*[-*+]\s+", "", RegexOptions.Multiline);
        // Remove ordered list markers
        text = Regex.Replace(text, @"^\s*\d+\.\s+", "", RegexOptions.Multiline);
        // Remove blockquote markers
        text = Regex.Replace(text, @"^\s*>\s*", "", RegexOptions.Multiline);
        return text.Trim();
    }

    private int GetParagraphIndexFromChar(int rawCharPos)
    {
        if (_paragraphSpans.Count == 0) return 0;
        var raw = MarkdownText.Replace("\r\n", "\n");
        rawCharPos = Math.Clamp(rawCharPos, 0, raw.Length);
        // Count double-newline paragraph boundaries before the cursor position
        int count = 0;
        int idx = 0;
        while (idx < rawCharPos)
        {
            int next = raw.IndexOf("\n\n", idx, StringComparison.Ordinal);
            if (next < 0 || next >= rawCharPos) break;
            count++;
            idx = next + 2;
        }
        return Math.Min(count, _paragraphSpans.Count - 1);
    }

    private async Task SpeakFromIndexAsync(int startIdx)
    {
        SaveLastReadParagraph(startIdx);
        var settings = _settingsService.Settings;
        if (string.IsNullOrWhiteSpace(settings.AzureSpeechApiKey) || string.IsNullOrWhiteSpace(settings.AzureSpeechRegion))
        {
            StatusText = "Azure Speech not configured. Go to Settings tab.";
            return;
        }
        if (_paragraphSpans.Count == 0)
        {
            StatusText = "No text loaded. Open a file first.";
            return;
        }

        var texts = _paragraphSpans
            .Skip(startIdx)
            .Select(p => p.text)
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .ToList();

        if (texts.Count == 0) return;

        IsSpeaking = true;
        IsSpeechPaused = false;
        _speechCts = new CancellationTokenSource();
        StatusText = startIdx > 0
            ? $"Reading from paragraph {startIdx + 1} of {_paragraphSpans.Count}..."
            : $"Reading {texts.Count} paragraphs...";

        try
        {
            await _speechService.SpeakParagraphsAsync(
                texts, ReadLanguage,
                settings.AzureSpeechApiKey, settings.AzureSpeechRegion,
                _speechCts.Token);
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

    public int GetRawCharIndexForParagraph(int paragraphIndex)
    {
        var raw = MarkdownText.Replace("\r\n", "\n");
        if (string.IsNullOrEmpty(raw) || paragraphIndex <= 0) return 0;
        int count = 0;
        int idx = 0;
        while (idx < raw.Length)
        {
            int next = raw.IndexOf("\n\n", idx, StringComparison.Ordinal);
            if (next < 0) break;
            count++;
            idx = next + 2;
            if (count >= paragraphIndex) return idx;
        }
        return raw.Length;
    }

    private string GetPreviewKey() => LoadedFilePath ?? "unsaved";

    private void SaveLastReadParagraph(int paragraphIndex)
    {
        if (_paragraphSpans.Count == 0) return;
        var key = GetPreviewKey();
        if (string.IsNullOrWhiteSpace(key)) return;
        var clamped = Math.Clamp(paragraphIndex, 0, _paragraphSpans.Count - 1);
        _settingsService.Settings.PreviewLastReadParagraphByFile[key] = clamped;
    }

    private void RestoreLastReadParagraph()
    {
        if (_paragraphSpans.Count == 0) return;
        var key = GetPreviewKey();
        if (string.IsNullOrWhiteSpace(key)) return;
        if (_settingsService.Settings.PreviewLastReadParagraphByFile.TryGetValue(key, out var idx))
            ScrollToParagraph = Math.Clamp(idx, 0, _paragraphSpans.Count - 1);
    }
}
