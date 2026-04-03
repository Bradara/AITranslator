using AITrans.Services;

namespace AITrans.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    private readonly SettingsService _settingsService;

    public SubtitlesViewModel SubtitlesTab { get; }
    public MarkdownViewModel MarkdownTab { get; }
    public MarkdownPreviewViewModel MarkdownPreviewTab { get; }
    public SettingsViewModel SettingsTab { get; }

    public MainViewModel()
    {
        _settingsService = new SettingsService();
        _settingsService.Load();
        var translationService = new TranslationService();
        var speechService = new SpeechService();
        var cacheService = new CacheService();

        SubtitlesTab = new SubtitlesViewModel(translationService, _settingsService, cacheService);
        MarkdownTab = new MarkdownViewModel(translationService, _settingsService, speechService, cacheService);
        MarkdownPreviewTab = new MarkdownPreviewViewModel(speechService, _settingsService);
        SettingsTab = new SettingsViewModel(_settingsService, translationService);
    }

    public void SaveState()
    {
        MarkdownPreviewTab.PersistSessionState();
    }
}
