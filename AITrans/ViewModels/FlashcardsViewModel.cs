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

public enum FlashcardMode
{
    Flip,             // Mode 1 – cycle through all three sides
    ForeignToNative,  // Mode 2 – show foreign word, type Bulgarian translation
    NativeToForeign   // Mode 3 – show Bulgarian word, type foreign translation
}

public partial class FlashcardsViewModel : ViewModelBase
{
    private readonly FlashcardService _flashcardService;
    private readonly SettingsService _settingsService;
    private readonly TranslationService _translationService;
    private readonly SpeechService _speechService;

    // ── TTS cancel tokens ─────────────────────────────────────────────────────
    private CancellationTokenSource? _speechCts;
    private CancellationTokenSource? _autoPlayCts;

    // ── Card collections ─────────────────────────────────────────────────────

    [ObservableProperty]
    private ObservableCollection<FlashCard> _cards = [];

    [ObservableProperty]
    private List<FlashCard> _quizDeck = [];

    // ── Navigation state ──────────────────────────────────────────────────────

    [ObservableProperty]
    private int _currentIndex;

    [ObservableProperty]
    private FlashCard? _currentCard;

    /// <summary>0 = front, 1 = back, 2 = usage (flip mode only).</summary>
    [ObservableProperty]
    private int _currentSide;

    // ── Mode ──────────────────────────────────────────────────────────────────

    [ObservableProperty]
    private FlashcardMode _mode = FlashcardMode.Flip;

    // RadioButton helpers – only setter with value==true changes the mode
    public bool IsFlipMode
    {
        get => Mode == FlashcardMode.Flip;
        set { if (value) Mode = FlashcardMode.Flip; }
    }

    public bool IsForeignToNativeMode
    {
        get => Mode == FlashcardMode.ForeignToNative;
        set { if (value) Mode = FlashcardMode.ForeignToNative; }
    }

    public bool IsNativeToForeignMode
    {
        get => Mode == FlashcardMode.NativeToForeign;
        set { if (value) Mode = FlashcardMode.NativeToForeign; }
    }

    public bool IsQuizMode => Mode is FlashcardMode.ForeignToNative or FlashcardMode.NativeToForeign;

    // Combined visibility helpers (mode + HasCards) to avoid duplicate IsVisible in AXAML
    public bool ShowFlipCard  => HasCards && Mode == FlashcardMode.Flip;
    public bool ShowQuizCard  => HasCards && IsQuizMode;

    // ── Add-card fields ───────────────────────────────────────────────────────

    [ObservableProperty]
    private string _newFrontText = "";

    [ObservableProperty]
    private string _newBackText = "";

    [ObservableProperty]
    private string _newUsageText = "";

    [ObservableProperty]
    private bool _isGeneratingAi;

    /// <summary>Displays which AI model will be used for card generation (the chat provider model).</summary>
    public string AiModelLabel =>
        $"Модел: {_settingsService.Settings.ChatActiveModel} ({_settingsService.Settings.EffectiveChatProvider})";

    // ── Quiz state ────────────────────────────────────────────────────────────

    [ObservableProperty]
    private string _userAnswer = "";

    [ObservableProperty]
    private string _answerFeedback = "";

    [ObservableProperty]
    private bool _isAnswerSubmitted;

    [ObservableProperty]
    private bool _isAnswerCorrect;

    // ── TTS state ─────────────────────────────────────────────────────────────

    [ObservableProperty]
    private bool _isSpeaking;

    [ObservableProperty]
    private bool _isAutoPlaying;

    /// <summary>Language for Side 1 (the foreign word).</summary>
    [ObservableProperty]
    private string _frontLanguage = "English";

    /// <summary>Language for Side 2 (the translation) — always Bulgarian by default.</summary>
    [ObservableProperty]
    private string _backLanguage = "Bulgarian";

    public string[] AvailableLanguages { get; } =
        ["Bulgarian", "Russian", "English", "German", "French", "Spanish"];

    // ── UI state ──────────────────────────────────────────────────────────────

    [ObservableProperty]
    private string _statusText = "";

    // ── Computed display properties ───────────────────────────────────────────

    public bool HasCards => QuizDeck.Count > 0;

    public string CardPositionText =>
        QuizDeck.Count == 0 ? "0 / 0" : $"{CurrentIndex + 1} / {QuizDeck.Count}";

    public string CurrentSideLabel => CurrentSide switch
    {
        0 => "Страна 1 — дума / израз",
        1 => "Страна 2 — превод",
        2 => "Страна 3 — употреба",
        _ => ""
    };

    public string CurrentSideText => CurrentCard == null ? "" : CurrentSide switch
    {
        0 => CurrentCard.FrontText,
        1 => CurrentCard.BackText,
        2 => CurrentCard.UsageText,
        _ => ""
    };

    public string QuizPromptText => CurrentCard == null ? "" : Mode switch
    {
        FlashcardMode.ForeignToNative => CurrentCard.FrontText,
        FlashcardMode.NativeToForeign => CurrentCard.BackText,
        _ => ""
    };

    public string QuizCorrectAnswer => CurrentCard == null ? "" : Mode switch
    {
        FlashcardMode.ForeignToNative => CurrentCard.BackText,
        FlashcardMode.NativeToForeign => CurrentCard.FrontText,
        _ => ""
    };

    // ── Constructor ───────────────────────────────────────────────────────────

    public FlashcardsViewModel(
        FlashcardService flashcardService,
        SettingsService settingsService,
        TranslationService translationService,
        SpeechService speechService)
    {
        _flashcardService   = flashcardService;
        _settingsService    = settingsService;
        _translationService = translationService;
        _speechService      = speechService;

        // Seed the front-language from the global speech source language setting
        var src = settingsService.Settings.SpeechSourceLanguage;
        if (!string.IsNullOrWhiteSpace(src) && AvailableLanguages.Contains(src))
            FrontLanguage = src;

        _ = LoadCardsAsync();
    }

    /// <summary>Exposes the service so the edit window can update/delete cards directly.</summary>
    public FlashcardService FlashcardService => _flashcardService;

    // ── Load ──────────────────────────────────────────────────────────────────

    private async Task LoadCardsAsync()
    {
        var cards = await _flashcardService.GetAllCardsAsync();
        Cards = new ObservableCollection<FlashCard>(cards);
        BuildQuizDeck();
        StatusText = $"Заредени {Cards.Count} карти.";
    }

    // ── Quiz deck ─────────────────────────────────────────────────────────────

    private void BuildQuizDeck()
    {
        QuizDeck = Mode == FlashcardMode.Flip
            ? [.. Cards]
            : [.. Cards.OrderByDescending(c => c.DifficultyScore).ThenByDescending(c => c.WrongCount)];

        CurrentIndex = 0;
        RefreshCurrentCard();
    }

    private void RefreshCurrentCard()
    {
        CurrentCard = QuizDeck.Count > 0 && CurrentIndex >= 0 && CurrentIndex < QuizDeck.Count
            ? QuizDeck[CurrentIndex]
            : null;

        CurrentSide      = 0;
        UserAnswer       = "";
        IsAnswerSubmitted = false;
        IsAnswerCorrect  = false;
        AnswerFeedback   = "";

        OnPropertyChanged(nameof(HasCards));
        OnPropertyChanged(nameof(CardPositionText));
        OnPropertyChanged(nameof(CurrentSideLabel));
        OnPropertyChanged(nameof(CurrentSideText));
        OnPropertyChanged(nameof(QuizPromptText));
        OnPropertyChanged(nameof(QuizCorrectAnswer));
        OnPropertyChanged(nameof(ShowFlipCard));
        OnPropertyChanged(nameof(ShowQuizCard));
    }

    // ── Mode change ───────────────────────────────────────────────────────────

    partial void OnModeChanged(FlashcardMode value)
    {
        OnPropertyChanged(nameof(IsFlipMode));
        OnPropertyChanged(nameof(IsForeignToNativeMode));
        OnPropertyChanged(nameof(IsNativeToForeignMode));
        OnPropertyChanged(nameof(IsQuizMode));
        OnPropertyChanged(nameof(ShowFlipCard));
        OnPropertyChanged(nameof(ShowQuizCard));
        BuildQuizDeck();
    }

    // ── Navigation commands ───────────────────────────────────────────────────

    [RelayCommand]
    private void NextCard()
    {
        if (QuizDeck.Count == 0) return;
        CurrentIndex = (CurrentIndex + 1) % QuizDeck.Count;
        RefreshCurrentCard();
    }

    [RelayCommand]
    private void PreviousCard()
    {
        if (QuizDeck.Count == 0) return;
        CurrentIndex = (CurrentIndex - 1 + QuizDeck.Count) % QuizDeck.Count;
        RefreshCurrentCard();
    }

    [RelayCommand]
    private void AdvanceSide()
    {
        if (CurrentCard == null) return;
        CurrentSide = (CurrentSide + 1) % 3;
        OnPropertyChanged(nameof(CurrentSideLabel));
        OnPropertyChanged(nameof(CurrentSideText));
    }

    [RelayCommand]
    private void Shuffle()
    {
        if (QuizDeck.Count == 0) return;
        var rng = new Random();
        QuizDeck = [.. QuizDeck.OrderBy(_ => rng.Next())];
        CurrentIndex = 0;
        RefreshCurrentCard();
    }

    // ── Add card commands ─────────────────────────────────────────────────────

    [RelayCommand]
    private async Task AddCard()
    {
        if (string.IsNullOrWhiteSpace(NewFrontText))
        {
            StatusText = "Въведете поне Страна 1 (думата).";
            return;
        }

        var card = new FlashCard
        {
            FrontText = NewFrontText.Trim(),
            BackText  = NewBackText.Trim(),
            UsageText = NewUsageText.Trim(),
            CreatedAt = DateTime.UtcNow
        };

        var id = await _flashcardService.SaveCardAsync(card);
        card.Id = id;
        Cards.Add(card);
        BuildQuizDeck();

        NewFrontText = "";
        NewBackText  = "";
        NewUsageText = "";
        StatusText   = "Картата е добавена.";
    }

    [RelayCommand]
    private async Task GenerateWithAi(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(NewFrontText))
        {
            StatusText = "Въведете думата в Страна 1 преди генериране.";
            return;
        }

        IsGeneratingAi = true;
        StatusText     = "Генериране с AI…";
        try
        {
            var settings = _settingsService.Settings;
            var (back, usage) = await _flashcardService.GenerateAiSidesAsync(
                NewFrontText.Trim(), _translationService, settings, ct);
            NewBackText  = back;
            NewUsageText = usage;
            StatusText   = "AI генерирането е завършено.";
        }
        catch (OperationCanceledException)
        {
            StatusText = "Операцията е отменена.";
        }
        catch (Exception ex)
        {
            StatusText = $"Грешка при AI генериране: {ex.Message}";
        }
        finally
        {
            IsGeneratingAi = false;
        }
    }

    /// <summary>Called from code-behind with the file path from the open-file dialog.</summary>
    [RelayCommand]
    private async Task ImportCsv(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return;

        StatusText = "Импортиране…";
        try
        {
            var imported = await _flashcardService.ImportFromCsvAsync(filePath);
            foreach (var card in imported)
                Cards.Add(card);
            BuildQuizDeck();
            StatusText = $"Импортирани {imported.Count} карти.";
        }
        catch (Exception ex)
        {
            StatusText = $"Грешка при импорт: {ex.Message}";
        }
    }

    // ── Quiz commands ─────────────────────────────────────────────────────────

    [RelayCommand]
    private async Task SubmitAnswer()
    {
        if (CurrentCard == null || IsAnswerSubmitted) return;

        var correctAnswer = QuizCorrectAnswer;

        // Split correct answer by comma/semicolon to support multiple accepted forms
        var accepted = correctAnswer
            .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .ToList();

        var userTrimmed = UserAnswer.Trim();
        var isCorrect = accepted.Any(a =>
            string.Equals(userTrimmed, a, StringComparison.OrdinalIgnoreCase));

        IsAnswerCorrect   = isCorrect;
        IsAnswerSubmitted = true;
        AnswerFeedback    = isCorrect
            ? "✓ Верно!"
            : $"✗ Грешно.  Правилен отговор: {correctAnswer}";

        await _flashcardService.UpdateStatsAsync(CurrentCard.Id, isCorrect);

        if (isCorrect)
            CurrentCard.CorrectCount++;
        else
            CurrentCard.WrongCount++;

        OnPropertyChanged(nameof(QuizCorrectAnswer));
    }

    // ── Card management commands ───────────────────────────────────────────────

    [RelayCommand]
    private async Task DeleteCard(FlashCard card)
    {
        await _flashcardService.DeleteCardAsync(card.Id);
        Cards.Remove(card);
        BuildQuizDeck();
        StatusText = "Картата е изтрита.";
    }

    /// <summary>Called by the view after the edit window closes to rebuild the quiz deck.</summary>
    public void RebuildAfterEdit()
    {
        BuildQuizDeck();
        StatusText = $"Карти: {Cards.Count}";
    }

    // ── TTS commands ──────────────────────────────────────────────────────────

    /// <summary>
    /// Speaks the text on the currently visible side using the appropriate language.
    /// Side 0 = FrontLanguage, Side 1 = BackLanguage, Side 2 = FrontLanguage (usage examples).
    /// </summary>
    [RelayCommand]
    private async Task SpeakCurrentSide(CancellationToken ct)
    {
        if (CurrentCard == null) return;

        var text = CurrentSideText;
        if (string.IsNullOrWhiteSpace(text)) return;

        // Side 1 (index 0) and Side 3 / usage (index 2) are in the foreign language;
        // Side 2 (translation, index 1) is in Bulgarian.
        var language = CurrentSide == 1 ? BackLanguage : FrontLanguage;

        await SpeakAsync([text], language, ct);
    }

    /// <summary>Stops any currently running TTS playback.</summary>
    [RelayCommand]
    private void StopSpeech()
    {
        _speechCts?.Cancel();
        _autoPlayCts?.Cancel();
    }

    /// <summary>
    /// Auto-play: for each card in the quiz deck, cycle through all three sides
    /// (speaking each one), then advance to the next card.
    /// </summary>
    [RelayCommand]
    private async Task StartAutoPlay(CancellationToken ct)
    {
        if (!HasCards || IsAutoPlaying) return;

        _autoPlayCts?.Dispose();
        _autoPlayCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = _autoPlayCts.Token;

        IsAutoPlaying = true;
        StatusText    = "Автоматично изговаряне…";

        try
        {
            // Start from the current position so user can resume mid-deck
            int startIndex = CurrentIndex;
            int total      = QuizDeck.Count;

            for (int i = 0; i < total; i++)
            {
                token.ThrowIfCancellationRequested();

                int idx = (startIndex + i) % total;
                CurrentIndex = idx;
                RefreshCurrentCard();

                if (CurrentCard == null) continue;

                // Speak all three sides in sequence
                var settings = _settingsService.Settings;
                string[] texts    = [CurrentCard.FrontText, CurrentCard.BackText, CurrentCard.UsageText];
                string[] languages = [FrontLanguage, BackLanguage, FrontLanguage];

                for (int side = 0; side < 3; side++)
                {
                    token.ThrowIfCancellationRequested();

                    if (string.IsNullOrWhiteSpace(texts[side])) continue;

                    // Advance the visual side indicator
                    CurrentSide = side;
                    OnPropertyChanged(nameof(CurrentSideLabel));
                    OnPropertyChanged(nameof(CurrentSideText));

                    await SpeakAsync([texts[side]], languages[side], token);

                    // Brief pause between sides
                    await Task.Delay(600, token);
                }

                // Short pause between cards
                await Task.Delay(1200, token);
            }

            StatusText = "Автоматично изговаряне — завършено.";
        }
        catch (OperationCanceledException)
        {
            StatusText = "Изговарянето е спряно.";
        }
        finally
        {
            IsAutoPlaying = false;
        }
    }

    private async Task SpeakAsync(IEnumerable<string> texts, string language, CancellationToken ct)
    {
        var settings = _settingsService.Settings;
        if (string.IsNullOrWhiteSpace(settings.AzureSpeechApiKey))
        {
            StatusText = "Въведете Azure Speech API ключ в настройките.";
            return;
        }

        _speechCts?.Dispose();
        _speechCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        IsSpeaking = true;
        try
        {
            await _speechService.SpeakParagraphsAsync(
                texts, language,
                settings.AzureSpeechApiKey, settings.AzureSpeechRegion,
                _speechCts.Token);
        }
        catch (OperationCanceledException) { /* stopped */ }
        catch (Exception ex)
        {
            StatusText = $"TTS грешка: {ex.Message}";
        }
        finally
        {
            IsSpeaking = false;
            _speechCts?.Dispose();
            _speechCts = null;
        }
    }
}
