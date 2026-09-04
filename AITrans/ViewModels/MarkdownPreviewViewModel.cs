using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AITrans.Models;
using AITrans.Services;

namespace AITrans.ViewModels;

public partial class MarkdownPreviewViewModel : ViewModelBase
{
    private readonly SpeechService _speechService;
    private readonly SettingsService _settingsService;
    private readonly CacheService _cacheService;
    private readonly EpubExportService _epubExportService;
    private readonly TranslationService _translationService;
    private readonly FlashcardService _flashcardService;
    private CancellationTokenSource? _speechCts;
    private CancellationTokenSource? _readTimerCts;
    // Live remaining time for the running countdown; null when no read timer is active.
    // Shared with IncreaseReadTimer/DecreaseReadTimer so the arrows can adjust it mid-read.
    private TimeSpan? _readTimerRemaining;
    private CancellationTokenSource? _chatCts;
    private readonly Stack<string> _navHistory = new();
    private bool _loadingFile;

    internal CacheService CacheService => _cacheService;

    // List of (charStart in PlainText, plain paragraph text)
    private List<(int charStart, string text)> _paragraphSpans = [];
    // Raw markdown paragraphs (unsplit, for pagination rendering)
    private List<string> _rawParagraphs = [];
    private int _selectionStart;
    private double _lastScrollRatio;

    // ──────────────────────────────────────────────────────
    //  Observable properties — document
    // ──────────────────────────────────────────────────────

    [ObservableProperty]
    private string _markdownText = "";

    [ObservableProperty]
    private string _currentPageMarkdown = "";

    [ObservableProperty]
    private string _plainText = "";

    [ObservableProperty]
    private int _scrollToParagraph = -1;

    [ObservableProperty]
    private bool _isSpeaking;

    [ObservableProperty]
    private bool _isSpeechPaused;

    /// <summary>Reading time limit as "M:SS" (or plain seconds). Doubles as the live
    /// countdown display while reading — restored to the original value once reading stops.</summary>
    [ObservableProperty]
    private string _readTimerInput = "5:00";

    [ObservableProperty]
    private bool _isExporting;

    [ObservableProperty]
    private string _statusText = "Ready — open a markdown file or paste text below";

    [ObservableProperty]
    private string? _loadedFilePath;

    [ObservableProperty]
    private string _readLanguage = "Bulgarian";

    [ObservableProperty]
    private double _previewFontSize = 18;

    [ObservableProperty]
    private bool _hasUnsavedChanges;

    // ──────────────────────────────────────────────────────
    //  Observable properties — pagination
    // ──────────────────────────────────────────────────────

    [ObservableProperty]
    private int _currentPage;

    [ObservableProperty]
    private int _totalPages = 1;

    [ObservableProperty]
    private int _paragraphsPerPage = 25;

    [ObservableProperty]
    private List<int> _pageNumbers = [1];

    [ObservableProperty]
    private int _selectedPageNumber = 1;

    public string PageIndicatorText => TotalPages == 0 ? "0 / 0" : $"{CurrentPage + 1} / {TotalPages}";

    // ──────────────────────────────────────────────────────
    //  Observable properties — word list
    // ──────────────────────────────────────────────────────

    [ObservableProperty]
    private ObservableCollection<WordListEntry> _wordList = [];

    public int WordListCount => WordList.Count;

    // ──────────────────────────────────────────────────────
    //  Observable properties — preview/edit toggle
    // ──────────────────────────────────────────────────────

    [ObservableProperty]
    private bool _isEditMode = false;

    // ──────────────────────────────────────────────────────
    //  Observable properties — AI chat
    // ──────────────────────────────────────────────────────

    [ObservableProperty]
    private string _chatInput = "";

    [ObservableProperty]
    private bool _isChatBusy;

    [ObservableProperty]
    private string _chatLanguage = "Bulgarian";

    [ObservableProperty]
    private string _translationProviderMode = "С ИИ";

    /// <summary>Incremented each time a new message is appended — view scrolls to bottom.</summary>
    [ObservableProperty]
    private int _chatScrollRequest;

    public ObservableCollection<ChatMessage> ChatMessages { get; } = [];

    public string[] AvailableLanguages { get; } = ["Bulgarian", "Russian", "English", "German", "French", "Spanish"];

    public string[] AvailableTranslationProviders { get; } = ["С ИИ", "DeepL", "Azure", "Google"];

    /// <summary>Shows which chat provider/model is active (read from settings at access time).</summary>
    public string ChatProviderModelDisplay
    {
        get
        {
            var s = _settingsService.Settings;
            var label = s.EffectiveChatProvider switch
            {
                AiProvider.GitHubCopilot => "GitHub Copilot",
                AiProvider.OpenRouter => "OpenRouter",
                AiProvider.Gemini => "Gemini",
                AiProvider.DeepSeek => "DeepSeek",
                AiProvider.Groq => "xAI",
                _ => "OpenAI"
            };
            return $"{label} / {s.ChatActiveModel}";
        }
}

    // ──────────────────────────────────────────────────────
    //  Constructor
    // ──────────────────────────────────────────────────────

    public MarkdownPreviewViewModel(
        SpeechService speechService,
        SettingsService settingsService,
        CacheService cacheService,
        EpubExportService epubExportService,
        TranslationService translationService,
        FlashcardService flashcardService,
        ObservableCollection<WordListEntry> sharedWordList)
    {
        _speechService = speechService;
        _settingsService = settingsService;
        _cacheService = cacheService;
        _epubExportService = epubExportService;
        _translationService = translationService;
        _flashcardService = flashcardService;

        WordList = sharedWordList;
        WordList.CollectionChanged += (_, _) => OnPropertyChanged(nameof(WordListCount));

        // Pre-populate language from settings if set
        var src = settingsService.Settings.SpeechSourceLanguage;
        if (!string.IsNullOrWhiteSpace(src) && AvailableLanguages.Contains(src))
            ReadLanguage = src;

        var defaultLang = settingsService.Settings.DefaultLanguage;
        if (!string.IsNullOrWhiteSpace(defaultLang) && AvailableLanguages.Contains(defaultLang))
            ChatLanguage = defaultLang;

        TranslationProviderMode = ResolveTranslationProviderMode(settingsService.Settings);
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

    // ──────────────────────────────────────────────────────
    //  EPUB Export
    // ──────────────────────────────────────────────────────

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
        RebuildPages();
    }

    partial void OnCurrentPageChanged(int value)
    {
        RebuildCurrentPage();
        SaveCurrentPageToSettings();
        SelectedPageNumber = value + 1;
        OnPropertyChanged(nameof(PageIndicatorText));
    }

    partial void OnSelectedPageNumberChanged(int value)
    {
        var pageIndex = value - 1;
        if (pageIndex >= 0 && pageIndex < TotalPages && pageIndex != CurrentPage)
            CurrentPage = pageIndex;
    }

    partial void OnParagraphsPerPageChanged(int value)
    {
        RebuildPages();
    }

    public void LoadFile(string path)
    {
        if (!string.IsNullOrEmpty(LoadedFilePath))
        {
            SaveChatHistory();
            _navHistory.Push(LoadedFilePath);
        }
        LoadFileCore(path);
        RegisterRecentFile(path);
        GoBackCommand.NotifyCanExecuteChanged();
    }

    private void LoadFileCore(string path)
    {
        _loadingFile = true;
        // Set the path before the content so GetPreviewKey() resolves to the new file
        // when MarkdownText's setter synchronously triggers RebuildPages() below.
        LoadedFilePath = path;
        MarkdownText = File.ReadAllText(path);  // triggers OnMarkdownTextChanged → BuildPlainText()
        _loadingFile = false;
        HasUnsavedChanges = false;
        StatusText = $"Loaded {_paragraphSpans.Count} paragraphs from {Path.GetFileName(path)}.";
        RestoreLastReadParagraph();
        LoadChatHistory(path);
        ChatInput = "";
    }

    public void PersistSessionState()
    {
        _settingsService.Settings.LastPreviewFilePath = LoadedFilePath ?? "";
        SaveChatHistory();
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
        SaveChatHistory();
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
    private async Task ReadAllAsync()
    {
        // Read from the start of the current page
        var pageOffset = CurrentPage * Math.Max(1, ParagraphsPerPage);
        await SpeakFromIndexAsync(pageOffset);
    }

    [RelayCommand]
    private async Task ReadFromSelectionAsync()
    {
        // GetParagraphIndexFromChar already resolves an absolute paragraph
        // index against the full document, so no extra page offset is needed.
        var idx = GetParagraphIndexFromChar(_selectionStart);
        await SpeakFromIndexAsync(idx);
    }

    /// <summary>Starts reading from the paragraph containing the given text (matched by content,
    /// so it works with a selection made in the rendered preview, not just the raw editor).</summary>
    public async Task ReadFromTextAsync(string? selectedText)
    {
        var idx = FindParagraphIndexForText(selectedText) ?? GetParagraphIndexFromChar(_selectionStart);
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

    private static readonly TimeSpan ReadTimerStep = TimeSpan.FromSeconds(10);

    [RelayCommand]
    private void IncreaseReadTimer() => AdjustReadTimer(ReadTimerStep);

    [RelayCommand]
    private void DecreaseReadTimer() => AdjustReadTimer(-ReadTimerStep);

    /// <summary>Adjusts the timer by <paramref name="delta"/>. While reading (including
    /// paused), this live-edits the running countdown; otherwise it edits the starting value.</summary>
    private void AdjustReadTimer(TimeSpan delta)
    {
        if (IsSpeaking)
        {
            var current = _readTimerRemaining ?? TimeSpan.Zero;
            var updated = current + delta;
            if (updated < TimeSpan.Zero) updated = TimeSpan.Zero;
            ReadTimerInput = FormatTimer(updated);

            if (updated <= TimeSpan.Zero)
            {
                _readTimerRemaining = updated;
                StopSpeech();
            }
            else if (_readTimerRemaining is null && _readTimerCts is not null)
            {
                // No countdown was running yet (e.g. reading started with no/invalid time
                // limit) — start one now so the new value actually takes effect.
                _ = RunReadTimerAsync(updated, _readTimerCts.Token);
            }
            else
            {
                _readTimerRemaining = updated;
            }
            return;
        }

        var baseTime = TryParseTimer(ReadTimerInput, out var parsed) ? parsed : TimeSpan.Zero;
        var next = baseTime + delta;
        if (next < TimeSpan.Zero) next = TimeSpan.Zero;
        ReadTimerInput = FormatTimer(next);
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

    [RelayCommand]
    private void ToggleEditMode()
    {
        IsEditMode = !IsEditMode;
    }

    // ──────────────────────────────────────────────────────
    //  Pagination commands
    // ──────────────────────────────────────────────────────

    [RelayCommand]
    private void NextPage()
    {
        if (CurrentPage < TotalPages - 1) CurrentPage++;
    }

    [RelayCommand]
    private void PreviousPage()
    {
        if (CurrentPage > 0) CurrentPage--;
    }

    [RelayCommand]
    private void FirstPage()
    {
        CurrentPage = 0;
    }

    [RelayCommand]
    private void LastPage()
    {
        if (TotalPages > 0) CurrentPage = TotalPages - 1;
    }

    // ──────────────────────────────────────────────────────
    //  Word list commands
    // ──────────────────────────────────────────────────────

    public async Task AddToWordListAsync(string word)
    {
        if (string.IsNullOrWhiteSpace(word)) return;
        var trimmed = word.Trim();
        if (WordList.Any(w => string.Equals(w.Word, trimmed, StringComparison.OrdinalIgnoreCase)))
        {
            StatusText = $"'{trimmed}' вече е в списъка.";
            return;
        }

        var entry = new WordListEntry
        {
            Word = trimmed,
            SourceFile = LoadedFilePath ?? "",
            AddedAt = DateTime.UtcNow
        };
        entry.Id = await _flashcardService.SaveWordEntryAsync(entry);
        WordList.Insert(0, entry);
        StatusText = $"'{trimmed}' добавено в списъка с думи.";
    }

    [RelayCommand]
    private async Task RemoveFromWordList(WordListEntry entry)
    {
        await _flashcardService.DeleteWordEntryAsync(entry.Id);
        WordList.Remove(entry);
    }


    [RelayCommand]
    private void ClearContext()
    {
        ChatInput = "";
    }

    [RelayCommand]
    private void ClearChat()
    {
        ChatMessages.Clear();
        SaveChatHistory();
    }

    [RelayCommand(CanExecute = nameof(CanSendChat))]
    private async Task SendChatAsync()
    {
        var userMessage = ChatInput.Trim();
        if (string.IsNullOrWhiteSpace(userMessage)) return;

        // Validate before clearing — preserves the user's text if not configured
        var settings = _settingsService.Settings;
        if (string.IsNullOrWhiteSpace(settings.ChatActiveApiKey) && settings.ChatProviderRequiresApiKey)
        {
            StatusText = "AI ключът не е конфигуриран. Отиди в Settings.";
            return;
        }

        ChatInput = "";
        await ExecuteChatActionAsync(userMessage);
    }

    private bool CanSendChat() => !IsChatBusy;

    // ──────────────────────────────────────────────────────
    //  AI Chat helpers
    // ──────────────────────────────────────────────────────

    /// <summary>
    /// Translates the given selected text using the currently chosen provider (AI chat, DeepL,
    /// Azure Translator, or Google Translate). Non-AI providers post the result into the chat
    /// panel as a user/assistant exchange, matching the AI translate flow's UX.
    /// </summary>
    internal async Task TranslateSelectionAsync(string text)
    {
        if (TranslationProviderMode == "С ИИ")
        {
            var extra = ChatInput.Trim();
            var prompt = string.IsNullOrEmpty(extra)
                ? $"Преведи на {ChatLanguage}:\n\n{text}"
                : $"Преведи на {ChatLanguage}:\n\n{text}\n\nДопълнителни инструкции: {extra}";
            await ExecuteChatActionAsync(prompt);
            return;
        }

        var settings = _settingsService.Settings;
        var provider = TranslationProviderMode;

        if (provider == "DeepL" && string.IsNullOrWhiteSpace(settings.DeepLApiKey))
        {
            StatusText = "DeepL API ключ не е зададен. Отиди в Settings.";
            return;
        }
        if (provider == "Azure" && string.IsNullOrWhiteSpace(settings.AzureTranslatorApiKey))
        {
            StatusText = "Azure Translator API ключ не е зададен. Отиди в Settings.";
            return;
        }

        ChatMessages.Add(new ChatMessage
        {
            Role = ChatRole.User,
            Content = $"Преведи ({provider}) на {ChatLanguage}:\n\n{text}"
        });
        ChatScrollRequest++;

        IsChatBusy = true;
        SendChatCommand.NotifyCanExecuteChanged();

        _chatCts?.Dispose();
        _chatCts = new CancellationTokenSource();

        try
        {
            var texts = new List<string> { text };
            var translations = provider switch
            {
                "DeepL" => await _translationService.TranslateDeepLBatchAsync(
                    texts, ChatLanguage, settings.DeepLApiKey, settings.DeepLFreeApi, ct: _chatCts.Token),
                "Azure" => await _translationService.TranslateAzureTranslatorBatchAsync(
                    texts, ChatLanguage, settings.AzureTranslatorApiKey, settings.AzureTranslatorEndpoint,
                    settings.AzureTranslatorRegion, ct: _chatCts.Token),
                "Google" => await _translationService.TranslateGoogleFreeBatchAsync(
                    texts, ChatLanguage, ct: _chatCts.Token),
                _ => texts
            };

            var reply = translations.Count > 0 ? translations[0] : "";
            ChatMessages.Add(new ChatMessage { Role = ChatRole.Assistant, Content = reply });
            ChatScrollRequest++;
            SaveChatHistory();
        }
        catch (OperationCanceledException)
        {
            // Translation cancelled — do nothing
        }
        catch (Exception ex)
        {
            ChatMessages.Add(new ChatMessage
            {
                Role = ChatRole.Assistant,
                Content = $"⚠️ Грешка: {ex.Message}"
            });
            ChatScrollRequest++;
        }
        finally
        {
            IsChatBusy = false;
            SendChatCommand.NotifyCanExecuteChanged();
        }
    }

    internal async Task ExecuteChatActionAsync(string userMessage)
    {
        var settings = _settingsService.Settings;
        if (string.IsNullOrWhiteSpace(settings.ChatActiveApiKey) && settings.ChatProviderRequiresApiKey)
        {
            StatusText = "AI ключът за чат не е конфигуриран. Отиди в Settings → Chat AI.";
            return;
        }

        var userMsg = new ChatMessage { Role = ChatRole.User, Content = userMessage };
        ChatMessages.Add(userMsg);
        ChatScrollRequest++;

        IsChatBusy = true;
        SendChatCommand.NotifyCanExecuteChanged();

        _chatCts?.Dispose();
        _chatCts = new CancellationTokenSource();

        try
        {
            var systemPrompt = BuildSystemPrompt();
            // Pass history excluding the message we just added (it's the new userMessage)
            var history = ChatMessages.Take(ChatMessages.Count - 1).ToList();

            var reply = await _translationService.ChatWithHistoryAsync(
                systemPrompt, history, userMessage, settings, _chatCts.Token);

            ChatMessages.Add(new ChatMessage { Role = ChatRole.Assistant, Content = reply });
            ChatScrollRequest++;
            SaveChatHistory();
        }
        catch (OperationCanceledException)
        {
            // Chat cancelled — do nothing
        }
        catch (Exception ex)
        {
            ChatMessages.Add(new ChatMessage
            {
                Role = ChatRole.Assistant,
                Content = $"⚠️ Грешка: {ex.Message}"
            });
            ChatScrollRequest++;
        }
        finally
        {
            IsChatBusy = false;
            SendChatCommand.NotifyCanExecuteChanged();
        }
    }

    private string BuildSystemPrompt()
    {
        return "You are a helpful document assistant. " +
               "You assist with translation, explanation, summarization, and analysis of text. " +
               "Be concise and clear. Respond in the same language the user uses, unless explicitly asked to translate.";
    }

    private void SaveChatHistory()
    {
        var key = GetPreviewKey();
        _settingsService.SaveChatHistory(key, [.. ChatMessages]);
    }

    private void LoadChatHistory(string path)
    {
        ChatMessages.Clear();
        var key = string.IsNullOrEmpty(path) ? "unsaved" : path;
        var all = _settingsService.LoadAllChatHistory();
        if (all.TryGetValue(key, out var messages) && messages.Count > 0)
        {
            foreach (var msg in messages)
                ChatMessages.Add(msg);
            ChatScrollRequest++;
        }
    }

    // ──────────────────────────────────────────────────────
    //  Private helpers
    // ──────────────────────────────────────────────────────

    private void BuildPlainText()
    {
        var raw = MarkdownText.Replace("\r\n", "\n");

        // Split on blank lines (paragraph boundaries) — keep raw chunks for pagination
        var rawChunks = raw.Split(["\n\n"], StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Trim())
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .ToList();
        _rawParagraphs = rawChunks;

        // Build plain text spans (stripped markdown for TTS)
        var parts = rawChunks.Select(StripMarkdown)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .ToList();

        _paragraphSpans = [];
        var sb = new StringBuilder();
        foreach (var p in parts)
        {
            _paragraphSpans.Add((sb.Length, p));
            sb.AppendLine(p);
            sb.AppendLine();
        }
        PlainText = sb.ToString();
    }

    private void RebuildPages()
    {
        if (_rawParagraphs.Count == 0)
        {
            TotalPages = 1;
            PageNumbers = [1];
            CurrentPage = 0;
            CurrentPageMarkdown = MarkdownText;
            OnPropertyChanged(nameof(PageIndicatorText));
            return;
        }

        var perPage = Math.Max(1, ParagraphsPerPage);
        TotalPages = (int)Math.Ceiling((double)_rawParagraphs.Count / perPage);
        PageNumbers = Enumerable.Range(1, TotalPages).ToList();

        // Restore saved page or clamp
        if (_loadingFile)
        {
            var key = GetPreviewKey();
            if (_settingsService.Settings.PreviewLastPageByFile.TryGetValue(key, out var savedPage))
                CurrentPage = Math.Clamp(savedPage, 0, TotalPages - 1);
            else
                CurrentPage = 0;
        }
        else
        {
            CurrentPage = Math.Clamp(CurrentPage, 0, TotalPages - 1);
        }

        RebuildCurrentPage();
        SelectedPageNumber = CurrentPage + 1;
        OnPropertyChanged(nameof(PageIndicatorText));
    }

    private void RebuildCurrentPage()
    {
        if (_rawParagraphs.Count == 0)
        {
            CurrentPageMarkdown = MarkdownText;
            return;
        }

        var perPage = Math.Max(1, ParagraphsPerPage);
        var startIdx = CurrentPage * perPage;
        var count = Math.Min(perPage, _rawParagraphs.Count - startIdx);
        if (startIdx >= _rawParagraphs.Count)
        {
            CurrentPageMarkdown = "";
            return;
        }

        CurrentPageMarkdown = string.Join("\n\n", _rawParagraphs.GetRange(startIdx, count));
    }

    private void SaveCurrentPageToSettings()
    {
        var key = GetPreviewKey();
        if (!string.IsNullOrWhiteSpace(key))
            _settingsService.Settings.PreviewLastPageByFile[key] = CurrentPage;
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
        rawCharPos = Math.Clamp(rawCharPos, 0, MarkdownText.Length);
        // _paragraphSpans/_rawParagraphs were built from a \r\n-normalized copy of
        // MarkdownText (see BuildPlainText), so a char offset taken from the editor
        // (which is measured against the original, possibly-CRLF text) must be
        // re-measured against that same normalized text before comparing.
        var normalizedPos = MarkdownText[..rawCharPos].Replace("\r\n", "\n").Length;
        var raw = MarkdownText.Replace("\r\n", "\n");
        // Count double-newline paragraph boundaries before the cursor position
        int count = 0;
        int idx = 0;
        while (idx < normalizedPos)
        {
            int next = raw.IndexOf("\n\n", idx, StringComparison.Ordinal);
            if (next < 0 || next >= normalizedPos) break;
            count++;
            idx = next + 2;
        }
        return Math.Min(count, _paragraphSpans.Count - 1);
    }

    /// <summary>Finds the index of the paragraph whose text contains the start of
    /// <paramref name="selectedText"/>, or null if no confident match is found.</summary>
    private int? FindParagraphIndexForText(string? selectedText)
    {
        if (string.IsNullOrWhiteSpace(selectedText) || _paragraphSpans.Count == 0) return null;

        var normalizedSelection = NormalizeForMatch(StripMarkdown(selectedText));
        // Require a few characters so short/common words don't match the wrong paragraph.
        if (normalizedSelection.Length < 4) return null;

        var snippet = normalizedSelection.Length > 60 ? normalizedSelection[..60] : normalizedSelection;

        for (int i = 0; i < _paragraphSpans.Count; i++)
        {
            var normalizedParagraph = NormalizeForMatch(_paragraphSpans[i].text);
            if (normalizedParagraph.Contains(snippet, StringComparison.OrdinalIgnoreCase))
                return i;
        }
        return null;
    }

    private static string NormalizeForMatch(string text) => Regex.Replace(text, @"\s+", " ").Trim();

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

        var originalTimerInput = ReadTimerInput;
        _readTimerCts = new CancellationTokenSource();
        if (TryParseTimer(originalTimerInput, out var timeLimit) && timeLimit > TimeSpan.Zero)
            _ = RunReadTimerAsync(timeLimit, _readTimerCts.Token);

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
            _readTimerCts?.Cancel();
            _readTimerCts?.Dispose();
            _readTimerCts = null;
            ReadTimerInput = originalTimerInput;
        }
    }

    /// <summary>Counts down <paramref name="duration"/> in one-second steps (frozen while
    /// paused), updating <see cref="ReadTimerInput"/> live, and stops reading at zero.
    /// The remaining time lives in <see cref="_readTimerRemaining"/> so IncreaseReadTimer/
    /// DecreaseReadTimer can adjust it mid-read (including while paused).</summary>
    private async Task RunReadTimerAsync(TimeSpan duration, CancellationToken token)
    {
        _readTimerRemaining = duration;
        try
        {
            while (_readTimerRemaining is { } remaining && remaining > TimeSpan.Zero)
            {
                await Task.Delay(1000, token);
                if (IsSpeechPaused) continue;
                remaining = (_readTimerRemaining ?? TimeSpan.Zero) - TimeSpan.FromSeconds(1);
                if (remaining < TimeSpan.Zero) remaining = TimeSpan.Zero;
                _readTimerRemaining = remaining;
                ReadTimerInput = FormatTimer(remaining);
            }
            StopSpeech();
        }
        catch (OperationCanceledException)
        {
            // Reading already ended before the timer ran out.
        }
        finally
        {
            _readTimerRemaining = null;
        }
    }

    private static bool TryParseTimer(string? text, out TimeSpan duration)
    {
        duration = TimeSpan.Zero;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var parts = text.Trim().Split(':');
        if (parts.Length == 2
            && int.TryParse(parts[0], out var minutes) && minutes >= 0
            && int.TryParse(parts[1], out var seconds) && seconds is >= 0 and < 60)
        {
            duration = new TimeSpan(0, minutes, seconds);
            return true;
        }
        if (parts.Length == 1 && int.TryParse(parts[0], out var secondsOnly) && secondsOnly >= 0)
        {
            duration = TimeSpan.FromSeconds(secondsOnly);
            return true;
        }
        return false;
    }

    private static string FormatTimer(TimeSpan duration) => $"{(int)duration.TotalMinutes}:{duration.Seconds:D2}";

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
