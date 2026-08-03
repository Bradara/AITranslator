using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
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
    [NotifyPropertyChangedFor(nameof(IsOllama))]
    [NotifyPropertyChangedFor(nameof(IsNvidia))]
    private string _selectedProvider = "OpenAI";

    public bool IsOpenAi => SelectedProvider == "OpenAI";
    public bool IsGitHubCopilot => SelectedProvider == "GitHub Copilot";
    public bool IsOpenRouter => SelectedProvider == "OpenRouter";
    public bool IsGemini => SelectedProvider == "Gemini";
    public bool IsDeepSeek => SelectedProvider == "DeepSeek";
    public bool IsGroq => SelectedProvider == "xAI";
    public bool IsOllama => SelectedProvider == "llama.cpp / LM Studio";
    public bool IsNvidia => SelectedProvider == "Nvidia";

    // ── Chat (AI Assistant) provider ──
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsChatOpenAi))]
    [NotifyPropertyChangedFor(nameof(IsChatGitHubCopilot))]
    [NotifyPropertyChangedFor(nameof(IsChatOpenRouter))]
    [NotifyPropertyChangedFor(nameof(IsChatGemini))]
    [NotifyPropertyChangedFor(nameof(IsChatDeepSeek))]
    [NotifyPropertyChangedFor(nameof(IsChatGroq))]
    [NotifyPropertyChangedFor(nameof(IsChatOllama))]
    [NotifyPropertyChangedFor(nameof(IsChatNvidia))]
    private string _selectedChatProvider = "OpenAI";

    public bool IsChatOpenAi => SelectedChatProvider == "OpenAI";
    public bool IsChatGitHubCopilot => SelectedChatProvider == "GitHub Copilot";
    public bool IsChatOpenRouter => SelectedChatProvider == "OpenRouter";
    public bool IsChatGemini => SelectedChatProvider == "Gemini";
    public bool IsChatDeepSeek => SelectedChatProvider == "DeepSeek";
    public bool IsChatGroq => SelectedChatProvider == "xAI";
    public bool IsChatOllama => SelectedChatProvider == "llama.cpp / LM Studio";
    public bool IsChatNvidia => SelectedChatProvider == "Nvidia";

    [ObservableProperty] private string _chatOpenAiModel = "gpt-4o-mini";
    [ObservableProperty] private string _chatGitHubCopilotModel = "gpt-4o";
    [ObservableProperty] private string _chatOpenRouterModel = "google/gemini-2.0-flash-exp:free";
    [ObservableProperty] private string _chatGeminiModel = "gemini-2.0-flash";
    [ObservableProperty] private string _chatDeepSeekModel = "deepseek-chat";
    [ObservableProperty] private string _chatGroqModel = "llama-3.3-70b-versatile";
    [ObservableProperty] private string _chatOllamaLmStudioModel = "llama3";
    [ObservableProperty] private string _chatNvidiaModel = "meta/llama-4-maverick-17b-128e-instruct";

    [ObservableProperty]
    private string _openAiApiKey = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGitHubCopilotSignedIn))]
    private string _gitHubCopilotApiKey = "";

    public bool IsGitHubCopilotSignedIn => !string.IsNullOrWhiteSpace(GitHubCopilotApiKey);

    [ObservableProperty]
    private bool _isGitHubSigningIn;

    [ObservableProperty]
    private string _gitHubDeviceCode = "";

    [ObservableProperty]
    private string _gitHubSignInStatus = "";

    [ObservableProperty]
    private string _openRouterApiKey = "";

    [ObservableProperty]
    private string _geminiApiKey = "";

    [ObservableProperty]
    private string _deepSeekApiKey = "";

    [ObservableProperty]
    private string _groqApiKey = "";

    [ObservableProperty]
    private string _nvidiaApiKey = "";

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
    private string _nvidiaModel = "meta/llama-4-maverick-17b-128e-instruct";

    [ObservableProperty]
    private string _ollamaLmStudioEndpoint = "http://127.0.0.1:8080/v1/chat/completions";

    [ObservableProperty]
    private string _ollamaLmStudioModel = "llama3";

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
    private string _ebookWorkingFolder = "";

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

    // Google Translate (free, unofficial endpoint)
    [ObservableProperty]
    private bool _useGoogleTranslateForMarkdown = false;

    // Azure Speech
    [ObservableProperty]
    private string _azureSpeechApiKey = "";

    [ObservableProperty]
    private string _azureSpeechRegion = "";

    [ObservableProperty]
    private string _speechSourceLanguage = "English";

    public string[] AvailableProviders { get; } = ["OpenAI", "GitHub Copilot", "OpenRouter", "Gemini", "DeepSeek", "xAI", "Nvidia", "llama.cpp / LM Studio"];

    public string[] NvidiaModels { get; } =
    [
        "meta/llama-4-maverick-17b-128e-instruct",
        "meta/llama-4-scout-17b-16e-instruct",
        "nvidia/llama-3.1-nemotron-ultra-253b-v1",
        "nvidia/llama-3.3-nemotron-super-49b-v1",
        "meta/llama-3.3-70b-instruct",
        "meta/llama-3.1-8b-instruct",
        "mistralai/mistral-large-2-instruct",
        "google/gemma-3-27b-it",
        "deepseek-ai/deepseek-r1",
        "qwen/qwen3-235b-a22b",
        "z-ai/glm-5.2",
        "google/gemma-4-31b-it",
        "nvidia/nemotron-3-ultra-550b-a55b"
    ];
    public string[] OpenAiModels { get; } = ["gpt-4o-mini", "gpt-4o", "gpt-4-turbo", "gpt-4.1-mini", "gpt-4.1", "gpt-4.1-nano"];
    public string[] DeepSeekModels { get; } = ["deepseek-chat", "deepseek-reasoner"];

    // GitHub Copilot inference endpoint selection.
    // GitHub Models (models.inference.ai.azure.com / models.github.ai — catalog, playground,
    // inference API and BYOK) was fully retired by GitHub on 2026-07-30; api.githubcopilot.com
    // is the only endpoint still live, so it's the only option left here.
    private static readonly string[] _ghEndpointLabels =
    [
        "GitHub Copilot API  (api.githubcopilot.com)",
    ];
    private static readonly string[] _ghEndpointUrls =
    [
        "https://api.githubcopilot.com/chat/completions",
    ];
    public string[] GitHubEndpointLabels => _ghEndpointLabels;

    [ObservableProperty]
    private string _gitHubCopilotEndpointLabel = _ghEndpointLabels[0];

    // Always included regardless of what the API returns.
    // claude-* / gemini-* models are not exposed by api.githubcopilot.com/models even on Pro
    // accounts (only surfaced inside the Copilot Chat UI), so only the always-available
    // baseline models are kept here — everything else should come from "Fetch models".
    private static readonly string[] _ghCopilotDefaults =
    [
        "gpt-4o", "gpt-4o-mini",
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

    [ObservableProperty]
    private ObservableCollection<string> _ollamaModels = [];

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
            AiProvider.GitHubCopilot  => "GitHub Copilot",
            AiProvider.OpenRouter     => "OpenRouter",
            AiProvider.Gemini         => "Gemini",
            AiProvider.DeepSeek       => "DeepSeek",
            AiProvider.Groq           => "xAI",
            AiProvider.OllamaLmStudio => "llama.cpp / LM Studio",
            AiProvider.Nvidia         => "Nvidia",
            _ => "OpenAI"
        };
        SelectedChatProvider = s.ChatProvider switch
        {
            AiProvider.GitHubCopilot  => "GitHub Copilot",
            AiProvider.OpenRouter     => "OpenRouter",
            AiProvider.Gemini         => "Gemini",
            AiProvider.DeepSeek       => "DeepSeek",
            AiProvider.Groq           => "xAI",
            AiProvider.OllamaLmStudio => "llama.cpp / LM Studio",
            AiProvider.Nvidia         => "Nvidia",
            _ => "OpenAI"
        };
        OpenAiApiKey = s.OpenAiApiKey;
        GitHubCopilotApiKey = s.GitHubCopilotApiKey;
        OpenRouterApiKey = s.OpenRouterApiKey;
        GeminiApiKey = s.GeminiApiKey;
        DeepSeekApiKey = s.DeepSeekApiKey;
        GroqApiKey = s.GroqApiKey;
        NvidiaApiKey = s.NvidiaApiKey;
        OpenAiModel = s.OpenAiModel;
        GitHubCopilotModel = s.GitHubCopilotModel;
        OpenRouterModel = s.OpenRouterModel;
        GeminiModel = s.GeminiModel;
        DeepSeekModel = s.DeepSeekModel;
        GroqModel = s.GroqModel;
        NvidiaModel = s.NvidiaModel;
        ChatOpenAiModel = s.ChatOpenAiModel;
        ChatGitHubCopilotModel = s.ChatGitHubCopilotModel;
        ChatOpenRouterModel = s.ChatOpenRouterModel;
        ChatGeminiModel = s.ChatGeminiModel;
        ChatDeepSeekModel = s.ChatDeepSeekModel;
        ChatGroqModel = s.ChatGroqModel;
        ChatOllamaLmStudioModel = s.ChatOllamaLmStudioModel;
        ChatNvidiaModel = s.ChatNvidiaModel;
        OllamaLmStudioEndpoint = s.OllamaLmStudioEndpoint;
        OllamaLmStudioModel = s.OllamaLmStudioModel;
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
        var mergedOllama = s.OllamaModels
            .Concat(new[] { s.OllamaLmStudioModel, s.ChatOllamaLmStudioModel, "llama3" })
            .Where(m => !string.IsNullOrWhiteSpace(m))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(m => m)
            .ToList();
        OllamaModels = new ObservableCollection<string>(mergedOllama);
        _settingsService.Settings.OllamaModels = mergedOllama;
        DeepLApiKey = s.DeepLApiKey;
        DeepLFreeApi = s.DeepLFreeApi;
        UseDeepLForMarkdown = s.UseDeepLForMarkdown;
        SelectedTheme = string.IsNullOrWhiteSpace(s.ThemeName) ? "System" : s.ThemeName;
        AzureTranslatorApiKey = s.AzureTranslatorApiKey;
        AzureTranslatorEndpoint = s.AzureTranslatorEndpoint;
        AzureTranslatorRegion = s.AzureTranslatorRegion;
        UseAzureTranslatorForMarkdown = s.UseAzureTranslatorForMarkdown;
        UseGoogleTranslateForMarkdown = s.UseGoogleTranslateForMarkdown;
        AzureSpeechApiKey = s.AzureSpeechApiKey;
        AzureSpeechRegion = s.AzureSpeechRegion;
        SpeechSourceLanguage = s.SpeechSourceLanguage;
        EbookWorkingFolder = s.EbookWorkingFolder;
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
    private async Task SignInWithGitHubAsync()
    {
        IsGitHubSigningIn = true;
        GitHubDeviceCode = "";
        GitHubSignInStatus = "Requesting a sign-in code from GitHub...";

        try
        {
            var device = await _translationService.StartGitHubDeviceFlowAsync();
            GitHubDeviceCode = device.UserCode;
            GitHubSignInStatus = $"Enter code {device.UserCode} at {device.VerificationUri} (opening browser...)";

            try { Process.Start(new ProcessStartInfo(device.VerificationUri) { UseShellExecute = true }); }
            catch { /* ignore — user can navigate there manually */ }

            var token = await _translationService.PollGitHubDeviceFlowAsync(device);

            GitHubCopilotApiKey = token;
            _settingsService.Settings.GitHubCopilotApiKey = token;
            _settingsService.Save();

            GitHubDeviceCode = "";
            GitHubSignInStatus = "Signed in with GitHub.";
            StatusText = "Signed in with GitHub Copilot.";
        }
        catch (Exception ex)
        {
            GitHubDeviceCode = "";
            GitHubSignInStatus = $"Sign-in failed: {ex.Message}";
        }
        finally
        {
            IsGitHubSigningIn = false;
        }
    }

    [RelayCommand]
    private void SignOutOfGitHub()
    {
        GitHubCopilotApiKey = "";
        _settingsService.Settings.GitHubCopilotApiKey = "";
        _settingsService.Save();
        GitHubSignInStatus = "Signed out.";
    }

    [RelayCommand]
    private async Task FetchGitHubModelsAsync()
    {
        if (string.IsNullOrWhiteSpace(GitHubCopilotApiKey))
        {
            StatusText = "Sign in with GitHub first.";
            return;
        }

        IsFetchingModels = true;
        StatusText = "Fetching models from GitHub Copilot...";

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
    private async Task FetchOllamaModelsAsync()
    {
        if (string.IsNullOrWhiteSpace(OllamaLmStudioEndpoint))
        {
            StatusText = "Enter a llama.cpp / LM Studio endpoint first.";
            return;
        }

        IsFetchingModels = true;
        StatusText = "Fetching models from llama.cpp / LM Studio...";

        try
        {
            var models = await _translationService.FetchOllamaModelsAsync(OllamaLmStudioEndpoint);

            var merged = OllamaModels
                .Concat(models)
                .Concat(new[] { OllamaLmStudioModel, ChatOllamaLmStudioModel })
                .Where(m => !string.IsNullOrWhiteSpace(m))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(m => m)
                .ToList();

            OllamaModels = new ObservableCollection<string>(merged);
            _settingsService.Settings.OllamaModels = merged;

            if (merged.Count > 0 && !merged.Contains(OllamaLmStudioModel, StringComparer.OrdinalIgnoreCase))
                OllamaLmStudioModel = merged[0];

            if (merged.Count > 0 && !merged.Contains(ChatOllamaLmStudioModel, StringComparer.OrdinalIgnoreCase))
                ChatOllamaLmStudioModel = merged[0];

            StatusText = $"Found {models.Count} llama.cpp / LM Studio models ({merged.Count} total).";
        }
        catch (Exception ex)
        {
            StatusText = $"Error fetching local models: {ex.Message}";
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
            "GitHub Copilot"     => AiProvider.GitHubCopilot,
            "OpenRouter"         => AiProvider.OpenRouter,
            "Gemini"             => AiProvider.Gemini,
            "DeepSeek"           => AiProvider.DeepSeek,
            "xAI"                => AiProvider.Groq,
            "llama.cpp / LM Studio" => AiProvider.OllamaLmStudio,
            "Nvidia"             => AiProvider.Nvidia,
            _ => AiProvider.OpenAI
        };
        s.OpenAiApiKey = OpenAiApiKey;
        s.GitHubCopilotApiKey = GitHubCopilotApiKey;
        s.OpenRouterApiKey = OpenRouterApiKey;
        s.GeminiApiKey = GeminiApiKey;
        s.DeepSeekApiKey = DeepSeekApiKey;
        s.GroqApiKey = GroqApiKey;
        s.NvidiaApiKey = NvidiaApiKey;
        s.OpenAiModel = OpenAiModel;
        s.GitHubCopilotModel = GitHubCopilotModel;
        s.OpenRouterModel = OpenRouterModel;
        s.GeminiModel = GeminiModel;
        s.DeepSeekModel = DeepSeekModel;
        s.GroqModel = GroqModel;
        s.NvidiaModel = NvidiaModel;
        s.GroqModels = [.. GroqModels];
        s.OllamaModels = [.. OllamaModels];
        s.ChatProvider = SelectedChatProvider switch
        {
            "GitHub Copilot"     => AiProvider.GitHubCopilot,
            "OpenRouter"         => AiProvider.OpenRouter,
            "Gemini"             => AiProvider.Gemini,
            "DeepSeek"           => AiProvider.DeepSeek,
            "xAI"                => AiProvider.Groq,
            "llama.cpp / LM Studio" => AiProvider.OllamaLmStudio,
            "Nvidia"             => AiProvider.Nvidia,
            _ => AiProvider.OpenAI
        };
        s.ChatOpenAiModel = ChatOpenAiModel;
        s.ChatGitHubCopilotModel = ChatGitHubCopilotModel;
        s.ChatOpenRouterModel = ChatOpenRouterModel;
        s.ChatGeminiModel = ChatGeminiModel;
        s.ChatDeepSeekModel = ChatDeepSeekModel;
        s.ChatGroqModel = ChatGroqModel;
        s.ChatOllamaLmStudioModel = ChatOllamaLmStudioModel;
        s.ChatNvidiaModel = ChatNvidiaModel;
        s.OllamaLmStudioEndpoint = OllamaLmStudioEndpoint;
        s.OllamaLmStudioModel = OllamaLmStudioModel;
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
        s.UseGoogleTranslateForMarkdown = UseGoogleTranslateForMarkdown;
        s.ThemeName = SelectedTheme;
        s.AzureSpeechApiKey = AzureSpeechApiKey;
        s.AzureSpeechRegion = AzureSpeechRegion;
        s.SpeechSourceLanguage = SpeechSourceLanguage;
        s.EbookWorkingFolder = EbookWorkingFolder;
        s.DefaultLanguage = DefaultLanguage;
        s.BatchSize = BatchSize;
        s.MarkdownBatchSize = MarkdownBatchSize;
        s.DelayBetweenRequestsMs = DelayBetweenRequestsMs;
        s.Temperature = Temperature;
        _settingsService.Save();
        StatusText = "Settings saved successfully.";
    }
}
