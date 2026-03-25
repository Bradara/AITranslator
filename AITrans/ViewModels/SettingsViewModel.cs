using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AITrans.Models;
using AITrans.Services;

namespace AITrans.ViewModels;

public partial class SettingsViewModel : ViewModelBase
{
    private readonly SettingsService _settingsService;
    private readonly TranslationService _translationService;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOpenAi))]
    [NotifyPropertyChangedFor(nameof(IsGitHubCopilot))]
    [NotifyPropertyChangedFor(nameof(IsOpenRouter))]
    [NotifyPropertyChangedFor(nameof(IsGemini))]
    [NotifyPropertyChangedFor(nameof(IsDeepSeek))]
    private string _selectedProvider = "OpenAI";

    public bool IsOpenAi => SelectedProvider == "OpenAI";
    public bool IsGitHubCopilot => SelectedProvider == "GitHub Copilot";
    public bool IsOpenRouter => SelectedProvider == "OpenRouter";
    public bool IsGemini => SelectedProvider == "Gemini";
    public bool IsDeepSeek => SelectedProvider == "DeepSeek";

    [ObservableProperty]
    private string _openAiApiKey = "";

    [ObservableProperty]
    private string _gitHubCopilotApiKey = "";

    [ObservableProperty]
    private string _openRouterApiKey = "";

    [ObservableProperty]
    private string _geminiApiKey = "";

    [ObservableProperty]
    private string _deepSeekApiKey = "";

    [ObservableProperty]
    private string _openAiModel = "gpt-4o-mini";

    [ObservableProperty]
    private string _gitHubCopilotModel = "gpt-4o";

    [ObservableProperty]
    private string _openRouterModel = "deepseek/deepseek-chat-v3-0324:free";

    [ObservableProperty]
    private string _geminiModel = "gemini-2.0-flash";

    [ObservableProperty]
    private string _deepSeekModel = "deepseek-chat";

    [ObservableProperty]
    private bool _openRouterAutoRotate = true;

    [ObservableProperty]
    private ObservableCollection<string> _openRouterModels = [];

    [ObservableProperty]
    private bool _isFetchingModels;

    [ObservableProperty]
    private string _freeModelCount = "";

    [ObservableProperty]
    private string _defaultLanguage = "Bulgarian";

    [ObservableProperty]
    private int _batchSize = 30;

    [ObservableProperty]
    private int _delayBetweenRequestsMs = 2000;

    [ObservableProperty]
    private string _statusText = "";

    // DeepL
    [ObservableProperty]
    private string _deepLApiKey = "";

    [ObservableProperty]
    private bool _deepLFreeApi = true;

    [ObservableProperty]
    private bool _useDeepLForMarkdown = false;

    // Azure Speech
    [ObservableProperty]
    private string _azureSpeechApiKey = "";

    [ObservableProperty]
    private string _azureSpeechRegion = "";

    [ObservableProperty]
    private string _speechSourceLanguage = "English";

    public string[] AvailableProviders { get; } = ["OpenAI", "GitHub Copilot", "OpenRouter", "Gemini", "DeepSeek"];
    public string[] OpenAiModels { get; } = ["gpt-4o-mini", "gpt-4o", "gpt-4-turbo", "gpt-4.1-mini", "gpt-4.1", "gpt-4.1-nano"];
    public string[] DeepSeekModels { get; } = ["deepseek-chat", "deepseek-reasoner"];

    // Always included regardless of what the API returns
    private static readonly string[] _ghCopilotDefaults =
    [
        "gpt-4o", "gpt-4o-mini",
        "claude-haiku-4.5", "claude-sonnet-4",
        "gemini-3-flash-preview",
    ];

    [ObservableProperty]
    private ObservableCollection<string> _gitHubCopilotModels = [];
    public string[] GeminiModels { get; } = [
        "gemini-2.0-flash",
        "gemini-2.5-flash",
        "gemini-2.5-pro",
        "gemini-1.5-flash",
        "gemini-1.5-pro",
    ];
    public string[] AvailableLanguages { get; } = ["Bulgarian", "Russian", "English"];

    [ObservableProperty]
    private string _customGitHubModel = "";

    [RelayCommand]
    private void AddCustomGitHubModel()
    {
        var m = CustomGitHubModel.Trim();
        if (string.IsNullOrEmpty(m)) return;
        if (!GitHubCopilotModels.Contains(m, StringComparer.OrdinalIgnoreCase))
        {
            GitHubCopilotModels.Add(m);
            _settingsService.Settings.GitHubCopilotModels = [.. GitHubCopilotModels];
        }
        GitHubCopilotModel = m;
        CustomGitHubModel = "";
    }

    public SettingsViewModel(SettingsService settingsService, TranslationService translationService)
    {
        _settingsService = settingsService;
        _translationService = translationService;
        var s = settingsService.Settings;
        SelectedProvider = s.Provider switch
        {
            AiProvider.GitHubCopilot => "GitHub Copilot",
            AiProvider.OpenRouter => "OpenRouter",
            AiProvider.Gemini => "Gemini",
            AiProvider.DeepSeek => "DeepSeek",
            _ => "OpenAI"
        };
        OpenAiApiKey = s.OpenAiApiKey;
        GitHubCopilotApiKey = s.GitHubCopilotApiKey;
        OpenRouterApiKey = s.OpenRouterApiKey;
        GeminiApiKey = s.GeminiApiKey;
        DeepSeekApiKey = s.DeepSeekApiKey;
        OpenAiModel = s.OpenAiModel;
        GitHubCopilotModel = s.GitHubCopilotModel;
        OpenRouterModel = s.OpenRouterModel;
        GeminiModel = s.GeminiModel;
        DeepSeekModel = s.DeepSeekModel;
        OpenRouterAutoRotate = s.OpenRouterAutoRotate;
        // Populate dropdowns from saved model lists
        // Merge saved list with hardcoded defaults (in case settings.json is missing some)
        var mergedOnLoad = s.GitHubCopilotModels
            .Concat(_ghCopilotDefaults)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(m => m)
            .ToList();
        GitHubCopilotModels = new ObservableCollection<string>(mergedOnLoad);
        _settingsService.Settings.GitHubCopilotModels = mergedOnLoad;
        OpenRouterModels = new ObservableCollection<string>(s.OpenRouterFreeModels);
        FreeModelCount = $"{s.OpenRouterFreeModels.Count} free models loaded";
        DeepLApiKey = s.DeepLApiKey;
        DeepLFreeApi = s.DeepLFreeApi;
        UseDeepLForMarkdown = s.UseDeepLForMarkdown;
        AzureSpeechApiKey = s.AzureSpeechApiKey;
        AzureSpeechRegion = s.AzureSpeechRegion;
        SpeechSourceLanguage = s.SpeechSourceLanguage;
        DefaultLanguage = s.DefaultLanguage;
        BatchSize = s.BatchSize;
        DelayBetweenRequestsMs = s.DelayBetweenRequestsMs;
    }

    [RelayCommand]
    private async Task FetchGitHubModelsAsync()
    {
        if (string.IsNullOrWhiteSpace(GitHubCopilotApiKey))
        {
            StatusText = "Enter GitHub Copilot API key first.";
            return;
        }

        IsFetchingModels = true;
        StatusText = "Fetching models from GitHub Models...";

        try
        {
            var models = await _translationService.FetchGitHubModelsAsync(GitHubCopilotApiKey);

            // Merge with existing list so hardcoded/user-added models are preserved
            var merged = GitHubCopilotModels
                .Concat(models)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(m => m)
                .ToList();

            GitHubCopilotModels = new ObservableCollection<string>(merged);
            _settingsService.Settings.GitHubCopilotModels = merged;

            if (merged.Count > 0 && !merged.Contains(GitHubCopilotModel, StringComparer.OrdinalIgnoreCase))
                GitHubCopilotModel = merged[0];

            StatusText = $"Found {models.Count} GitHub models ({merged.Count} total).";
        }
        catch (Exception ex)
        {
            StatusText = $"Error fetching models: {ex.Message}";
        }
        finally
        {
            IsFetchingModels = false;
        }
    }

    [RelayCommand]
    private async Task FetchFreeModelsAsync()
    {
        if (string.IsNullOrWhiteSpace(OpenRouterApiKey))
        {
            StatusText = "Enter OpenRouter API key first.";
            return;
        }

        IsFetchingModels = true;
        StatusText = "Fetching free models from OpenRouter...";

        try
        {
            var models = await _translationService.FetchOpenRouterFreeModelsAsync(OpenRouterApiKey);

            // Replace the collection atomically to avoid ComboBox popup issues
            OpenRouterModels = new ObservableCollection<string>(models);

            // Update the settings list too
            _settingsService.Settings.OpenRouterFreeModels = models;

            if (models.Count > 0 && !models.Contains(OpenRouterModel))
                OpenRouterModel = models[0];

            FreeModelCount = $"{models.Count} free models available";
            StatusText = $"Found {models.Count} free models.";
        }
        catch (Exception ex)
        {
            StatusText = $"Error fetching models: {ex.Message}";
        }
        finally
        {
            IsFetchingModels = false;
        }
    }

    [RelayCommand]
    private void Save()
    {
        var s = _settingsService.Settings;
        s.Provider = SelectedProvider switch
        {
            "GitHub Copilot" => AiProvider.GitHubCopilot,
            "OpenRouter" => AiProvider.OpenRouter,
            "Gemini" => AiProvider.Gemini,
            "DeepSeek" => AiProvider.DeepSeek,
            _ => AiProvider.OpenAI
        };
        s.OpenAiApiKey = OpenAiApiKey;
        s.GitHubCopilotApiKey = GitHubCopilotApiKey;
        s.OpenRouterApiKey = OpenRouterApiKey;
        s.GeminiApiKey = GeminiApiKey;
        s.DeepSeekApiKey = DeepSeekApiKey;
        s.OpenAiModel = OpenAiModel;
        s.GitHubCopilotModel = GitHubCopilotModel;
        s.OpenRouterModel = OpenRouterModel;
        s.GeminiModel = GeminiModel;
        s.DeepSeekModel = DeepSeekModel;
        s.OpenRouterAutoRotate = OpenRouterAutoRotate;
        s.DeepLApiKey = DeepLApiKey;
        s.DeepLFreeApi = DeepLFreeApi;
        s.UseDeepLForMarkdown = UseDeepLForMarkdown;
        s.AzureSpeechApiKey = AzureSpeechApiKey;
        s.AzureSpeechRegion = AzureSpeechRegion;
        s.SpeechSourceLanguage = SpeechSourceLanguage;
        s.DefaultLanguage = DefaultLanguage;
        s.BatchSize = BatchSize;
        s.DelayBetweenRequestsMs = DelayBetweenRequestsMs;
        _settingsService.Save();
        StatusText = "Settings saved successfully.";
    }
}
