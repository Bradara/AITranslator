using AITrans.Services;

namespace AITrans.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    public SubtitlesViewModel SubtitlesTab { get; }
    public MarkdownViewModel MarkdownTab { get; }
    public SettingsViewModel SettingsTab { get; }

    public MainViewModel()
    {
        var settingsService = new SettingsService();
        settingsService.Load();
        var translationService = new TranslationService();

        SubtitlesTab = new SubtitlesViewModel(translationService, settingsService);
        MarkdownTab = new MarkdownViewModel(translationService, settingsService);
        SettingsTab = new SettingsViewModel(settingsService, translationService);
    }
}
