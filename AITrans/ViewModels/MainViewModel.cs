using AITrans.Services;

namespace AITrans.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    private readonly SettingsService _settingsService;
    private readonly ThemeService _themeService;

    public SubtitlesViewModel SubtitlesTab { get; }
    public MarkdownViewModel MarkdownTab { get; }
    public MarkdownPreviewViewModel MarkdownPreviewTab { get; }
    public SettingsViewModel SettingsTab { get; }

    public MainViewModel(SettingsService settingsService, ThemeService themeService)
    {
        _settingsService = settingsService;
        _themeService = themeService;
        var translationService = new TranslationService();
        var speechService = new SpeechService();
        var cacheService = new CacheService();
        var epubExportService = new EpubExportService();

        SubtitlesTab = new SubtitlesViewModel(translationService, _settingsService, cacheService);
        MarkdownTab = new MarkdownViewModel(translationService, _settingsService, speechService, cacheService);
        MarkdownPreviewTab = new MarkdownPreviewViewModel(speechService, _settingsService, cacheService, epubExportService);
        SettingsTab = new SettingsViewModel(_settingsService, translationService, _themeService);
    }

    public void SaveState()
    {
        SubtitlesTab.PersistSessionState();
        MarkdownTab.PersistSessionState();
        MarkdownPreviewTab.PersistSessionState();
    }
}
