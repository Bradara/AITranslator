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
    private CancellationTokenSource? _speechCts;
    private readonly Stack<string> _navHistory = new();
    private bool _loadingFile;

    // List of (charStart in PlainText, plain paragraph text)
    private List<(int charStart, string text)> _paragraphSpans = [];
    private int _selectionStart;

    [ObservableProperty]
    private string _markdownText = "";

    [ObservableProperty]
    private string _plainText = "";

    [ObservableProperty]
    private bool _isSpeaking;

    [ObservableProperty]
    private string _statusText = "Ready — open a markdown file or paste text below";

    [ObservableProperty]
    private string? _loadedFilePath;

    [ObservableProperty]
    private string _readLanguage = "English";

    [ObservableProperty]
    private double _previewFontSize = 16;

    [ObservableProperty]
    private bool _hasUnsavedChanges;

    public string[] AvailableLanguages { get; } = ["Bulgarian", "Russian", "English", "German", "French", "Spanish"];

    public MarkdownPreviewViewModel(SpeechService speechService, SettingsService settingsService)
    {
        _speechService = speechService;
        _settingsService = settingsService;

        // Pre-populate language from settings if set
        var src = settingsService.Settings.SpeechSourceLanguage;
        if (!string.IsNullOrWhiteSpace(src) && AvailableLanguages.Contains(src))
            ReadLanguage = src;
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
        GoBackCommand.NotifyCanExecuteChanged();
    }

    public void SaveToFile(string path)
    {
        File.WriteAllText(path, MarkdownText);
        LoadedFilePath = path;
        HasUnsavedChanges = false;
        StatusText = $"Saved {Path.GetFileName(path)}.";
    }

    /// <summary>Called from code-behind when the user moves the caret in the raw editor.</summary>
    public void SetSelectionStart(int charPos) => _selectionStart = charPos;

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
            _speechCts?.Dispose();
            _speechCts = null;
        }
    }
}
