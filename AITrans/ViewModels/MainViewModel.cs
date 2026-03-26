using AITrans.Services;

namespace AITrans.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    public SubtitlesViewModel SubtitlesTab { get; }
    public MarkdownViewModel MarkdownTab { get; }
    public MarkdownPreviewViewModel MarkdownPreviewTab { get; }
    public SettingsViewModel SettingsTab { get; }

    public MainViewModel()
    {
        var settingsService = new SettingsService();
        settingsService.Load();
        var translationService = new TranslationService();
        var speechService = new SpeechService();
        var cacheService = new CacheService();

        SubtitlesTab = new SubtitlesViewModel(translationService, settingsService, cacheService);
        MarkdownTab = new MarkdownViewModel(translationService, settingsService, speechService, cacheService);
        MarkdownPreviewTab = new MarkdownPreviewViewModel(speechService, settingsService);
        SettingsTab = new SettingsViewModel(settingsService, translationService);
    }
}
