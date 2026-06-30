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
    ForeignToNative,  // Mode 2 – type the translation (direction controlled by ReverseQuizDirection)
    MultipleChoice,   // Mode 3 – show foreign word, pick translation from 4 options
    Listen,           // Mode 4 – hear the word via TTS, type the translation
    ListenChoice      // Mode 5 – hear the word via TTS, pick translation from 4 options
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

    // ── Difficulty filter ────────────────────────────────────────────────────

    [ObservableProperty]
    private DifficultyOption _selectedDifficulty = DifficultyOption.Options[1]; // Unlearned (previous default behavior)

    public DifficultyOption[] DifficultyOptions { get; } = DifficultyOption.Options;

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

    public bool IsMultipleChoiceMode
    {
        get => Mode == FlashcardMode.MultipleChoice;
        set { if (value) Mode = FlashcardMode.MultipleChoice; }
    }

    public bool IsListenMode
    {
        get => Mode == FlashcardMode.Listen;
        set { if (value) Mode = FlashcardMode.Listen; }
    }

    public bool IsListenChoiceMode
    {
        get => Mode == FlashcardMode.ListenChoice;
        set { if (value) Mode = FlashcardMode.ListenChoice; }
    }

    /// <summary>True for modes that use a text-answer quiz card (type-in).</summary>
    public bool IsQuizMode => Mode is FlashcardMode.ForeignToNative or FlashcardMode.Listen;

    /// <summary>True for modes that use multiple-choice buttons (MC or ListenChoice).</summary>
    public bool IsChoiceMode => Mode is FlashcardMode.MultipleChoice or FlashcardMode.ListenChoice;

    /// <summary>When true the quiz prompt shows BackText (BG) and expects FrontText (foreign).</summary>
    [ObservableProperty]
    private bool _reverseQuizDirection;

    // Combined visibility helpers (mode + HasCards) to avoid duplicate IsVisible in AXAML
    public bool ShowFlipCard       => HasCards && Mode == FlashcardMode.Flip;
    public bool ShowQuizCard       => HasCards && Mode == FlashcardMode.ForeignToNative;
    public bool ShowMultipleChoice => HasCards && Mode == FlashcardMode.MultipleChoice;
    public bool ShowListenCard     => HasCards && Mode == FlashcardMode.Listen;
    public bool ShowListenChoice   => HasCards && Mode == FlashcardMode.ListenChoice;

    /// <summary>Show rating buttons in flip mode after the back has been revealed at least once.</summary>
    public bool ShowFlipRating     => Mode == FlashcardMode.Flip && HasCards && CurrentSide >= 1;

    public bool IsCurrentRatingHard    => CurrentCard?.Rating == CardRating.Hard;
    public bool IsCurrentRatingNormal  => CurrentCard?.Rating == CardRating.Normal;
    public bool IsCurrentRatingEasy    => CurrentCard?.Rating == CardRating.Easy;
    public bool IsCurrentRatingLearned => CurrentCard?.Rating == CardRating.Learned;

    /// <summary>Number of cards excluded from the deck because they are marked Learned.</summary>
    public int LearnedCount        => Cards.Count(c => c.Rating == CardRating.Learned);
    public int ActiveCount         => QuizDeck.Count;

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

    // ── Multiple-choice state ─────────────────────────────────────────────────

    [ObservableProperty]
    private ObservableCollection<ChoiceOption> _choices = [];

    [ObservableProperty]
    private bool _isMcAnswerSubmitted;

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

    // ── Word list state ─────────────────────────────────────────────────────────

    [ObservableProperty]
    private ObservableCollection<WordListEntry> _wordList = [];

    [ObservableProperty]
    private bool _isWordListVisible;

    public int WordListCount => WordList.Count;

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

    public string QuizPromptText  => CurrentCard == null ? "" :
        ReverseQuizDirection ? CurrentCard.BackText : CurrentCard.FrontText;

    public string QuizCorrectAnswer => CurrentCard == null ? "" :
        ReverseQuizDirection ? CurrentCard.FrontText : CurrentCard.BackText;

    // ── Constructor ───────────────────────────────────────────────────────────

    public FlashcardsViewModel(
        FlashcardService flashcardService,
        SettingsService settingsService,
        TranslationService translationService,
        SpeechService speechService,
        ObservableCollection<WordListEntry> sharedWordList)
    {
        _flashcardService   = flashcardService;
        _settingsService    = settingsService;
        _translationService = translationService;
        _speechService      = speechService;

        WordList = sharedWordList;
        WordList.CollectionChanged += (_, _) => OnPropertyChanged(nameof(WordListCount));
        if (WordList.Count > 0) IsWordListVisible = true;

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
        var filtered = DifficultyFilter.Filter(Cards, SelectedDifficulty.Level);
        QuizDeck = [.. InterleaveByRating(filtered)];
        CurrentIndex = 0;
        OnPropertyChanged(nameof(LearnedCount));
        OnPropertyChanged(nameof(ActiveCount));
        RefreshCurrentCard();
    }

    /// <summary>
    /// Round-robin interleave across rating groups so the user sees one Hard card,
    /// then one New, one Normal, one Easy, then Hard again — instead of all Hard
    /// cards exhausted before any Normal/Easy cards appear.
    /// Within each group, hardest cards (by DifficultyScore) appear first.
    /// Group order: Hard → New → Normal → Easy.
    /// </summary>
    private static IEnumerable<FlashCard> InterleaveByRating(List<FlashCard> cards)
    {
        static int GroupPriority(CardRating r) => r switch
        {
            CardRating.Hard   => 0,
            CardRating.New    => 1,
            CardRating.Normal => 2,
            CardRating.Easy   => 3,
            _                 => 99
        };

        var groups = cards
            .GroupBy(c => c.Rating)
            .OrderBy(g => GroupPriority(g.Key))
            .Select(g => g
                .OrderByDescending(c => c.DifficultyScore)
                .ThenByDescending(c => c.WrongCount - c.CorrectCount)
                .ThenByDescending(c => c.CreatedAt)
                .ToList())
            .ToList();

        if (groups.Count == 0) yield break;

        int maxLen = groups.Max(g => g.Count);
        for (int i = 0; i < maxLen; i++)
            foreach (var group in groups)
                if (i < group.Count)
                    yield return group[i];
    }

    private void RefreshCurrentCard()
    {
        CurrentCard = QuizDeck.Count > 0 && CurrentIndex >= 0 && CurrentIndex < QuizDeck.Count
            ? QuizDeck[CurrentIndex]
            : null;

        CurrentSide       = 0;
        UserAnswer        = "";
        IsAnswerSubmitted = false;
        IsAnswerCorrect   = false;
        AnswerFeedback    = "";
        IsMcAnswerSubmitted = false;

        OnPropertyChanged(nameof(HasCards));
        OnPropertyChanged(nameof(CardPositionText));
        OnPropertyChanged(nameof(CurrentSideLabel));
        OnPropertyChanged(nameof(CurrentSideText));
        OnPropertyChanged(nameof(QuizPromptText));
        OnPropertyChanged(nameof(QuizCorrectAnswer));
        OnPropertyChanged(nameof(ShowFlipCard));
        OnPropertyChanged(nameof(ShowQuizCard));
        OnPropertyChanged(nameof(ShowMultipleChoice));
        OnPropertyChanged(nameof(ShowListenCard));
        OnPropertyChanged(nameof(ShowListenChoice));
        OnPropertyChanged(nameof(ShowFlipRating));
        OnPropertyChanged(nameof(IsCurrentRatingHard));
        OnPropertyChanged(nameof(IsCurrentRatingNormal));
        OnPropertyChanged(nameof(IsCurrentRatingEasy));
        OnPropertyChanged(nameof(IsCurrentRatingLearned));

        if (IsChoiceMode)
            BuildChoices();
        else
            Choices = [];

        if (Mode is FlashcardMode.Listen or FlashcardMode.ListenChoice && CurrentCard != null)
            _ = SpeakListenPromptAsync();
    }

    // ── Mode change ───────────────────────────────────────────────────────────

    partial void OnModeChanged(FlashcardMode value)
    {
        OnPropertyChanged(nameof(IsFlipMode));
        OnPropertyChanged(nameof(IsForeignToNativeMode));
        OnPropertyChanged(nameof(IsMultipleChoiceMode));
        OnPropertyChanged(nameof(IsListenMode));
        OnPropertyChanged(nameof(IsListenChoiceMode));
        OnPropertyChanged(nameof(IsQuizMode));
        OnPropertyChanged(nameof(IsChoiceMode));
        OnPropertyChanged(nameof(ShowFlipCard));
        OnPropertyChanged(nameof(ShowQuizCard));
        OnPropertyChanged(nameof(ShowMultipleChoice));
        OnPropertyChanged(nameof(ShowListenCard));
        OnPropertyChanged(nameof(ShowListenChoice));
        OnPropertyChanged(nameof(ShowFlipRating));
        BuildQuizDeck();
    }

    partial void OnSelectedDifficultyChanged(DifficultyOption value) => BuildQuizDeck();

    partial void OnReverseQuizDirectionChanged(bool value)
    {
        OnPropertyChanged(nameof(QuizPromptText));
        OnPropertyChanged(nameof(QuizCorrectAnswer));
        // Reset current answer when direction flips
        UserAnswer          = "";
        IsAnswerSubmitted   = false;
        IsAnswerCorrect     = false;
        AnswerFeedback      = "";
        IsMcAnswerSubmitted = false;
        if (IsChoiceMode)
            BuildChoices();
        if (Mode is FlashcardMode.Listen or FlashcardMode.ListenChoice && CurrentCard != null)
            _ = SpeakListenPromptAsync();
    }

    // ── Navigation commands ───────────────────────────────────────────────────

    [RelayCommand]
    private void NextCard()
    {
        if (QuizDeck.Count == 0) return;
        bool isLast = CurrentIndex >= QuizDeck.Count - 1;
        if (isLast)
        {
            // Rebuild with updated ratings so the next pass reflects any changes
            BuildQuizDeck();
        }
        else
        {
            CurrentIndex++;
            RefreshCurrentCard();
        }
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
        OnPropertyChanged(nameof(ShowFlipRating));
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

    /// <summary>Called from code-behind with the file path from the save-file dialog.</summary>
    [RelayCommand]
    private async Task ExportCsv(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return;

        StatusText = "Експортиране…";
        try
        {
            await _flashcardService.ExportToCsvAsync(filePath, Cards);
            StatusText = $"Експортирани {Cards.Count} карти.";
        }
        catch (Exception ex)
        {
            StatusText = $"Грешка при експорт: {ex.Message}";
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
            .Split([',', ';', '.', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => s.Length > 1)
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

    // ── Multiple-choice helpers ───────────────────────────────────────────────

    private static readonly Random _rng = new();

    private void BuildChoices()
    {
        if (CurrentCard == null) { Choices = []; return; }

        const int total = 4;

        // The "answer" text shown on the buttons depends on direction
        string correctText    = ReverseQuizDirection ? CurrentCard.FrontText : CurrentCard.BackText;
        string correctField(FlashCard c) => ReverseQuizDirection ? c.FrontText : c.BackText;

        var distractors = QuizDeck
            .Where(c => c.Id != CurrentCard.Id &&
                        !string.IsNullOrWhiteSpace(correctField(c)) &&
                        correctField(c) != correctText)
            .OrderBy(_ => _rng.Next())
            .Take(total - 1)
            .Select(c => new ChoiceOption { Text = correctField(c), IsCorrectAnswer = false })
            .ToList();

        var correctOption = new ChoiceOption { Text = correctText, IsCorrectAnswer = true };

        var allOptions = distractors.Append(correctOption).OrderBy(_ => _rng.Next()).ToList();
        Choices = new ObservableCollection<ChoiceOption>(allOptions);
    }

    [RelayCommand]
    private async Task SelectChoice(ChoiceOption option)
    {
        if (IsMcAnswerSubmitted || CurrentCard == null) return;

        IsMcAnswerSubmitted = true;

        // Reveal all options
        foreach (var c in Choices)
        {
            if (c.IsCorrectAnswer)
                c.State = ChoiceState.Correct;
            else if (ReferenceEquals(c, option) && !c.IsCorrectAnswer)
                c.State = ChoiceState.Wrong;
        }

        var isCorrect = option.IsCorrectAnswer;
        await _flashcardService.UpdateStatsAsync(CurrentCard.Id, isCorrect);

        if (isCorrect)
            CurrentCard.CorrectCount++;
        else
            CurrentCard.WrongCount++;
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

    // ── Rating command ────────────────────────────────────────────────────────

    /// <summary>
    /// Rates the current card and advances to the next one.
    /// The deck is NOT reordered mid-session — we simply advance to the next card.
    /// On the next full cycle (wrap-around), BuildQuizDeck rebuilds with updated
    /// ratings so Easy cards naturally fall later in the round-robin order.
    /// Learned cards are removed immediately via a full rebuild.
    /// </summary>
    [RelayCommand]
    private async Task RateCard(CardRating rating)
    {
        if (CurrentCard == null) return;

        var card = CurrentCard;
        card.Rating = rating;
        await _flashcardService.UpdateRatingAsync(card.Id, rating);

        if (rating == CardRating.Learned)
        {
            BuildQuizDeck();
            StatusText = $"Заучена! Оставащи: {QuizDeck.Count}";
            return;
        }

        // Just advance — deck order is preserved for this pass
        NextCard();
    }

    [RelayCommand] private Task RateHard()    => RateCard(CardRating.Hard);
    [RelayCommand] private Task RateNormal()  => RateCard(CardRating.Normal);
    [RelayCommand] private Task RateEasy()    => RateCard(CardRating.Easy);
    [RelayCommand] private Task RateLearned() => RateCard(CardRating.Learned);

    // ── TTS commands ──────────────────────────────────────────────────────────

    /// <summary>
    /// Speaks the listen-mode prompt: the word that the user must translate.
    /// Direction-aware: FrontText (foreign) normally, BackText (BG) when reversed.
    /// </summary>
    [RelayCommand]
    private async Task SpeakListenPrompt(CancellationToken ct)
    {
        if (CurrentCard == null) return;
        await SpeakListenPromptAsync(ct);
    }

    private async Task SpeakListenPromptAsync(CancellationToken ct = default)
    {
        if (CurrentCard == null) return;
        var text = ReverseQuizDirection ? CurrentCard.BackText : CurrentCard.FrontText;
        var lang = ReverseQuizDirection ? BackLanguage : FrontLanguage;
        if (string.IsNullOrWhiteSpace(text)) return;
        await SpeakAsync([text], lang, ct);
    }

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

    // ── Word list commands ────────────────────────────────────────────────────

    [RelayCommand]
    private void UseWord(WordListEntry entry)
    {
        NewFrontText = entry.Word;
        StatusText = $"'{entry.Word}' заредено в Страна 1. Натиснете 'Генерирай с AI' или попълнете ръчно.";
    }

    [RelayCommand]
    private async Task RemoveWord(WordListEntry entry)
    {
        await _flashcardService.DeleteWordEntryAsync(entry.Id);
        WordList.Remove(entry);
    }

    [RelayCommand]
    private async Task ClearWordList()
    {
        await _flashcardService.ClearWordListAsync();
        WordList.Clear();
        StatusText = "Списъкът с думи е изчистен.";
    }

    [RelayCommand]
    private void ToggleWordList()
    {
        IsWordListVisible = !IsWordListVisible;
    }
}
