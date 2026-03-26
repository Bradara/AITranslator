using System;
using System.Collections.Generic;
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
        BuildPlainText();
        StatusText = $"Loaded {_paragraphSpans.Count} paragraphs.";
    }

    public void LoadFile(string path)
    {
        var content = File.ReadAllText(path);
        LoadedFilePath = path;
        MarkdownText = content;
        BuildPlainText();
        StatusText = $"Loaded {_paragraphSpans.Count} paragraphs from {Path.GetFileName(path)}.";
    }

    /// <summary>Called from code-behind when the user moves the caret/selection in the plain text box.</summary>
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

    private int GetParagraphIndexFromChar(int charPos)
    {
        for (int i = _paragraphSpans.Count - 1; i >= 0; i--)
        {
            if (charPos >= _paragraphSpans[i].charStart)
                return i;
        }
        return 0;
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
