using System.Collections.ObjectModel;
using AITrans.Models;
using AITrans.Services;

namespace AITrans.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    private readonly SettingsService _settingsService;
    private readonly ThemeService _themeService;

    public SubtitlesViewModel SubtitlesTab { get; }
    public MarkdownViewModel MarkdownTab { get; }
    public MarkdownPreviewViewModel MarkdownPreviewTab { get; }
    public AiChatViewModel AiChatTab { get; }
    public SettingsViewModel SettingsTab { get; }
    public FlashcardsViewModel FlashcardsTab { get; }
    public MemoryGameViewModel MemoryGameTab { get; }

    public MainViewModel(SettingsService settingsService, ThemeService themeService)
    {
        _settingsService = settingsService;
        _themeService = themeService;
        var translationService = new TranslationService();
        var speechService = new SpeechService();
        var cacheService = new CacheService();
        var epubExportService = new EpubExportService();
        var ebookImportService = new EbookImportService();
        var flashcardService = new FlashcardService(cacheService);

        // Shared word list — one collection used by both Preview and Flashcards
        var sharedWordList = new ObservableCollection<WordListEntry>(cacheService.GetAllWordListEntries());

        SubtitlesTab = new SubtitlesViewModel(translationService, _settingsService, cacheService);
        MarkdownTab = new MarkdownViewModel(translationService, _settingsService, speechService, cacheService, ebookImportService);
        MarkdownPreviewTab = new MarkdownPreviewViewModel(speechService, _settingsService, cacheService, epubExportService, translationService, flashcardService, sharedWordList);
        AiChatTab = new AiChatViewModel(translationService, _settingsService);
        SettingsTab = new SettingsViewModel(_settingsService, translationService, _themeService);
        FlashcardsTab = new FlashcardsViewModel(flashcardService, _settingsService, translationService, speechService, sharedWordList);
        MemoryGameTab = new MemoryGameViewModel(flashcardService);
    }

    public void SaveState()
    {
        SubtitlesTab.PersistSessionState();
        MarkdownTab.PersistSessionState();
        MarkdownPreviewTab.PersistSessionState();
        AiChatTab.PersistSessionState();
    }
}
