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
    private readonly ThemeService _themeService;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOpenAi))]
    [NotifyPropertyChangedFor(nameof(IsGitHubCopilot))]
    [NotifyPropertyChangedFor(nameof(IsOpenRouter))]
    [NotifyPropertyChangedFor(nameof(IsGemini))]
    [NotifyPropertyChangedFor(nameof(IsDeepSeek))]
    [NotifyPropertyChangedFor(nameof(IsGroq))]
    private string _selectedProvider = "OpenAI";

    public bool IsOpenAi => SelectedProvider == "OpenAI";
    public bool IsGitHubCopilot => SelectedProvider == "GitHub Copilot";
    public bool IsOpenRouter => SelectedProvider == "OpenRouter";
    public bool IsGemini => SelectedProvider == "Gemini";
    public bool IsDeepSeek => SelectedProvider == "DeepSeek";
    public bool IsGroq => SelectedProvider == "xAI";

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
    private string _groqApiKey = "";

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
    private string _groqModel = "llama-3.3-70b-versatile";

    [ObservableProperty]
    private bool _openRouterAutoRotate = true;

    [ObservableProperty]
    private ObservableCollection<string> _openRouterModels = [];

    [ObservableProperty]
    private bool _isFetchingModels;

    [ObservableProperty]
    private string _freeModelCount = "";

    [ObservableProperty]
    private string _selectedTheme = "System";

    [ObservableProperty]
    private string _defaultLanguage = "Bulgarian";

    [ObservableProperty]
    private int _batchSize = 30;

    [ObservableProperty]
    private int _markdownBatchSize = 10;

    [ObservableProperty]
    private int _delayBetweenRequestsMs = 2000;

    [ObservableProperty]
    private double _temperature = 1.0;

    [ObservableProperty]
    private string _statusText = "";

    // DeepL
    [ObservableProperty]
    private string _deepLApiKey = "";

    [ObservableProperty]
    private bool _deepLFreeApi = true;

    [ObservableProperty]
    private bool _useDeepLForMarkdown = false;

    // Azure AI Translator
    [ObservableProperty]
    private string _azureTranslatorApiKey = "";

    [ObservableProperty]
    private string _azureTranslatorEndpoint = "https://api.cognitive.microsofttranslator.com";

    [ObservableProperty]
    private string _azureTranslatorRegion = "";

    [ObservableProperty]
    private bool _useAzureTranslatorForMarkdown = false;

    // Azure Speech
    [ObservableProperty]
    private string _azureSpeechApiKey = "";

    [ObservableProperty]
    private string _azureSpeechRegion = "";

    [ObservableProperty]
    private string _speechSourceLanguage = "English";

    public string[] AvailableProviders { get; } = ["OpenAI", "GitHub Copilot", "OpenRouter", "Gemini", "DeepSeek", "xAI"];
    public string[] OpenAiModels { get; } = ["gpt-4o-mini", "gpt-4o", "gpt-4-turbo", "gpt-4.1-mini", "gpt-4.1", "gpt-4.1-nano"];
    public string[] DeepSeekModels { get; } = ["deepseek-chat", "deepseek-reasoner"];

    // GitHub Copilot inference endpoint selection
    private static readonly string[] _ghEndpointLabels =
    [
        "Azure Inference  (models.inference.ai.azure.com)",
        "GitHub Copilot API  (api.githubcopilot.com)",
        "GitHub AI  (models.github.ai)",
    ];
    private static readonly string[] _ghEndpointUrls =
    [
        "https://models.inference.ai.azure.com/chat/completions",
        "https://api.githubcopilot.com/chat/completions",
        "https://models.github.ai/inference/chat/completions",
    ];
    public string[] GitHubEndpointLabels => _ghEndpointLabels;

    [ObservableProperty]
    private string _gitHubCopilotEndpointLabel = _ghEndpointLabels[0];

    // Always included regardless of what the API returns
    private static readonly string[] _ghCopilotDefaults =
    [
        "gpt-4o", "gpt-4o-mini",
        "claude-haiku-4.5", "claude-sonnet-4",
        "gemini-3-flash-preview",
    ];

    [ObservableProperty]
    private ObservableCollection<string> _gitHubCopilotModels = [];

    // Groq models
    private static readonly string[] _groqDefaults =
    [
        "grok-4-1-fast-reasoning",
        "grok-4-1-fast-non-reasoning",
    ];

    [ObservableProperty]
    private ObservableCollection<string> _groqModels = [];

    [ObservableProperty]
    private string _customGroqModel = "";

    [RelayCommand]
    private void AddCustomGroqModel()
    {
        var m = CustomGroqModel.Trim();
        if (string.IsNullOrEmpty(m)) return;
        if (!GroqModels.Contains(m, StringComparer.OrdinalIgnoreCase))
        {
            GroqModels.Add(m);
            _settingsService.Settings.GroqModels = [.. GroqModels];
        }
        GroqModel = m;
        CustomGroqModel = "";
    }

    [RelayCommand]
    private void RemoveGroqModel()
    {
        if (string.IsNullOrEmpty(GroqModel)) return;
        var toRemove = GroqModel;
        var idx = GroqModels.IndexOf(toRemove);
        GroqModels.Remove(toRemove);
        _settingsService.Settings.GroqModels = [.. GroqModels];
        if (GroqModels.Count > 0)
            GroqModel = GroqModels[Math.Max(0, idx - 1)];
    }

    public string[] GeminiModels { get; } = [
        "gemini-2.0-flash",
        "gemini-2.5-flash",
        "gemini-2.5-pro",
        "gemini-1.5-flash",
        "gemini-1.5-pro",
    ];
    public string[] AvailableThemes { get; } = [
        "System", "Light", "Dark",
        "Dracula", "Molokai",
        "Solarized Dark", "Solarized Light",
        "Papyrus", "Papyrus Contrast", "Sand"
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

    [RelayCommand]
    private void RemoveGitHubModel()
    {
        if (string.IsNullOrEmpty(GitHubCopilotModel)) return;
        var toRemove = GitHubCopilotModel;
        var idx = GitHubCopilotModels.IndexOf(toRemove);
        GitHubCopilotModels.Remove(toRemove);
        _settingsService.Settings.GitHubCopilotModels = [.. GitHubCopilotModels];
        if (GitHubCopilotModels.Count > 0)
            GitHubCopilotModel = GitHubCopilotModels[Math.Max(0, idx - 1)];
    }

    public SettingsViewModel(SettingsService settingsService, TranslationService translationService, ThemeService themeService)
    {
        _settingsService = settingsService;
        _translationService = translationService;
        _themeService = themeService;
        var s = settingsService.Settings;
        SelectedProvider = s.Provider switch
        {
            AiProvider.GitHubCopilot => "GitHub Copilot",
            AiProvider.OpenRouter => "OpenRouter",
            AiProvider.Gemini => "Gemini",
            AiProvider.DeepSeek => "DeepSeek",
            AiProvider.Groq => "xAI",
            _ => "OpenAI"
        };
        OpenAiApiKey = s.OpenAiApiKey;
        GitHubCopilotApiKey = s.GitHubCopilotApiKey;
        OpenRouterApiKey = s.OpenRouterApiKey;
        GeminiApiKey = s.GeminiApiKey;
        DeepSeekApiKey = s.DeepSeekApiKey;
        GroqApiKey = s.GroqApiKey;
        OpenAiModel = s.OpenAiModel;
        GitHubCopilotModel = s.GitHubCopilotModel;
        OpenRouterModel = s.OpenRouterModel;
        GeminiModel = s.GeminiModel;
        DeepSeekModel = s.DeepSeekModel;
        GroqModel = s.GroqModel;
        OpenRouterAutoRotate = s.OpenRouterAutoRotate;
        // Load GitHub endpoint label from saved URL
        var urlIdx = Array.IndexOf(_ghEndpointUrls, s.GitHubCopilotInferenceUrl);
        GitHubCopilotEndpointLabel = _ghEndpointLabels[urlIdx >= 0 ? urlIdx : 0];
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
        // Groq models
        var mergedGroq = s.GroqModels
            .Concat(_groqDefaults)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(m => m)
            .ToList();
        GroqModels = new ObservableCollection<string>(mergedGroq);
        _settingsService.Settings.GroqModels = mergedGroq;
        DeepLApiKey = s.DeepLApiKey;
        DeepLFreeApi = s.DeepLFreeApi;
        UseDeepLForMarkdown = s.UseDeepLForMarkdown;
        SelectedTheme = string.IsNullOrWhiteSpace(s.ThemeName) ? "System" : s.ThemeName;
        AzureTranslatorApiKey = s.AzureTranslatorApiKey;
        AzureTranslatorEndpoint = s.AzureTranslatorEndpoint;
        AzureTranslatorRegion = s.AzureTranslatorRegion;
        UseAzureTranslatorForMarkdown = s.UseAzureTranslatorForMarkdown;
        AzureSpeechApiKey = s.AzureSpeechApiKey;
        AzureSpeechRegion = s.AzureSpeechRegion;
        SpeechSourceLanguage = s.SpeechSourceLanguage;
        DefaultLanguage = s.DefaultLanguage;
        BatchSize = s.BatchSize;
        MarkdownBatchSize = s.MarkdownBatchSize;
        DelayBetweenRequestsMs = s.DelayBetweenRequestsMs;
        Temperature = s.Temperature;
    }

    partial void OnSelectedThemeChanged(string value)
    {
        _themeService.ApplyTheme(value);
    }

    [RelayCommand]
    private async Task FetchGroqModelsAsync()
    {
        if (string.IsNullOrWhiteSpace(GroqApiKey))
        {
            StatusText = "Enter xAI API key first.";
            return;
        }

        IsFetchingModels = true;
        StatusText = "Fetching models from xAI...";

        try
        {
            var models = await _translationService.FetchGroqModelsAsync(GroqApiKey);

            var merged = GroqModels
                .Concat(models)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(m => m)
                .ToList();

            GroqModels = new ObservableCollection<string>(merged);
            _settingsService.Settings.GroqModels = merged;

            if (merged.Count > 0 && !merged.Contains(GroqModel, StringComparer.OrdinalIgnoreCase))
                GroqModel = merged[0];

            StatusText = $"Found {models.Count} xAI models ({merged.Count} total).";
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
            "xAI" => AiProvider.Groq,
            _ => AiProvider.OpenAI
        };
        s.OpenAiApiKey = OpenAiApiKey;
        s.GitHubCopilotApiKey = GitHubCopilotApiKey;
        s.OpenRouterApiKey = OpenRouterApiKey;
        s.GeminiApiKey = GeminiApiKey;
        s.DeepSeekApiKey = DeepSeekApiKey;
        s.GroqApiKey = GroqApiKey;
        s.OpenAiModel = OpenAiModel;
        s.GitHubCopilotModel = GitHubCopilotModel;
        s.OpenRouterModel = OpenRouterModel;
        s.GeminiModel = GeminiModel;
        s.DeepSeekModel = DeepSeekModel;
        s.GroqModel = GroqModel;
        s.GroqModels = [.. GroqModels];
        s.OpenRouterAutoRotate = OpenRouterAutoRotate;
        // GitHub Copilot inference endpoint
        var labelIdx = Array.IndexOf(_ghEndpointLabels, GitHubCopilotEndpointLabel);
        s.GitHubCopilotInferenceUrl = _ghEndpointUrls[labelIdx >= 0 ? labelIdx : 0];
        s.DeepLApiKey = DeepLApiKey;
        s.DeepLFreeApi = DeepLFreeApi;
        s.UseDeepLForMarkdown = UseDeepLForMarkdown;
        s.AzureTranslatorApiKey = AzureTranslatorApiKey;
        s.AzureTranslatorEndpoint = AzureTranslatorEndpoint;
        s.AzureTranslatorRegion = AzureTranslatorRegion;
        s.UseAzureTranslatorForMarkdown = UseAzureTranslatorForMarkdown;
        s.ThemeName = SelectedTheme;
        s.AzureSpeechApiKey = AzureSpeechApiKey;
        s.AzureSpeechRegion = AzureSpeechRegion;
        s.SpeechSourceLanguage = SpeechSourceLanguage;
        s.DefaultLanguage = DefaultLanguage;
        s.BatchSize = BatchSize;
        s.MarkdownBatchSize = MarkdownBatchSize;
        s.DelayBetweenRequestsMs = DelayBetweenRequestsMs;
        s.Temperature = Temperature;
        _settingsService.Save();
        StatusText = "Settings saved successfully.";
    }
}
