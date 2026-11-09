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
    // Suppresses the dropdown OnChanged auto-save handlers while the constructor populates
    // properties from saved settings — those assignments aren't user edits.
    private bool _loadingSettings = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOpenAi))]
    [NotifyPropertyChangedFor(nameof(IsGitHubCopilot))]
    [NotifyPropertyChangedFor(nameof(IsOpenRouter))]
    [NotifyPropertyChangedFor(nameof(IsGemini))]
    [NotifyPropertyChangedFor(nameof(IsDeepSeek))]
    [NotifyPropertyChangedFor(nameof(IsGroq))]
    [NotifyPropertyChangedFor(nameof(IsOllama))]
    [NotifyPropertyChangedFor(nameof(IsQwen))]
    private string _selectedProvider = "OpenAI";

    public bool IsOpenAi => SelectedProvider == "OpenAI";
    public bool IsGitHubCopilot => SelectedProvider == "GitHub Copilot";
    public bool IsOpenRouter => SelectedProvider == "OpenRouter";
    public bool IsGemini => SelectedProvider == "Gemini";
    public bool IsDeepSeek => SelectedProvider == "DeepSeek";
    public bool IsGroq => SelectedProvider == "xAI";
    public bool IsOllama => SelectedProvider == "llama.cpp / LM Studio";
    public bool IsQwen => SelectedProvider == "Qwen";

    // ── Chat (AI Assistant) provider ──
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsChatOpenAi))]
    [NotifyPropertyChangedFor(nameof(IsChatGitHubCopilot))]
    [NotifyPropertyChangedFor(nameof(IsChatOpenRouter))]
    [NotifyPropertyChangedFor(nameof(IsChatGemini))]
    [NotifyPropertyChangedFor(nameof(IsChatDeepSeek))]
    [NotifyPropertyChangedFor(nameof(IsChatGroq))]
    [NotifyPropertyChangedFor(nameof(IsChatOllama))]
    [NotifyPropertyChangedFor(nameof(IsChatQwen))]
    private string _selectedChatProvider = "OpenAI";

    public bool IsChatOpenAi => SelectedChatProvider == "OpenAI";
    public bool IsChatGitHubCopilot => SelectedChatProvider == "GitHub Copilot";
    public bool IsChatOpenRouter => SelectedChatProvider == "OpenRouter";
    public bool IsChatGemini => SelectedChatProvider == "Gemini";
    public bool IsChatDeepSeek => SelectedChatProvider == "DeepSeek";
    public bool IsChatGroq => SelectedChatProvider == "xAI";
    public bool IsChatOllama => SelectedChatProvider == "llama.cpp / LM Studio";
    public bool IsChatQwen => SelectedChatProvider == "Qwen";

    [ObservableProperty] private string _chatOpenAiModel = "gpt-4o-mini";
    [ObservableProperty] private string _chatGitHubCopilotModel = "gpt-4o";
    [ObservableProperty] private string _chatOpenRouterModel = "google/gemini-2.0-flash-exp:free";
    [ObservableProperty] private string _chatGeminiModel = "gemini-2.0-flash";
    [ObservableProperty] private string _chatDeepSeekModel = "deepseek-chat";
    [ObservableProperty] private string _chatGroqModel = "llama-3.3-70b-versatile";
    [ObservableProperty] private string _chatOllamaLmStudioModel = "llama3";
    [ObservableProperty] private string _chatQwenModel = "qwen-plus";

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
    private string _qwenApiKey = "";

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
    private string _qwenModel = "qwen-plus";

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

    public string[] AvailableProviders { get; } = ["OpenAI", "GitHub Copilot", "OpenRouter", "Gemini", "DeepSeek", "xAI", "Qwen", "llama.cpp / LM Studio"];

    public string[] OpenAiModels { get; } = ["gpt-4o-mini", "gpt-4o", "gpt-4-turbo", "gpt-4.1-mini", "gpt-4.1", "gpt-4.1-nano"];
    public string[] DeepSeekModels { get; } = ["deepseek-chat", "deepseek-reasoner"];

    // GitHub Copilot inference endpoint selection.
    // The local Copilot CLI (driven through the official GitHub.Copilot.SDK) is only offered on
    // desktop, where it can be spawned as a child process. GitHub Models (models.inference.ai.azure.com
    // / models.github.ai) was fully retired by GitHub on 2026-07-30.
    private static readonly string[] _ghEndpointLabels = CopilotCliService.IsSupported
        ? ["Local GitHub Copilot CLI  (official SDK)", "GitHub Copilot API  (api.githubcopilot.com)"]
        : ["GitHub Copilot API  (api.githubcopilot.com)"];
    private static readonly string[] _ghEndpointUrls = CopilotCliService.IsSupported
        ? [CopilotCliService.CliEndpoint, "https://api.githubcopilot.com/chat/completions"]
        : ["https://api.githubcopilot.com/chat/completions"];
    public string[] GitHubEndpointLabels => _ghEndpointLabels;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGitHubCopilotCliMode))]
    [NotifyPropertyChangedFor(nameof(IsGitHubCopilotApiMode))]
    private string _gitHubCopilotEndpointLabel = _ghEndpointLabels[0];

    private string SelectedGitHubEndpointUrl
    {
        get
        {
            var idx = Array.IndexOf(_ghEndpointLabels, GitHubCopilotEndpointLabel);
            return _ghEndpointUrls[idx >= 0 ? idx : 0];
        }
    }

    public bool IsGitHubCopilotCliMode => CopilotCliService.IsCliEndpoint(SelectedGitHubEndpointUrl);
    public bool IsGitHubCopilotApiMode => !IsGitHubCopilotCliMode;

    // Empty = auto-detect the CLI on PATH / npm / winget.
    [ObservableProperty]
    private string _gitHubCopilotCliPath = "";

    [ObservableProperty]
    private string _gitHubCopilotCliStatus = "";

    [ObservableProperty]
    private bool _isCheckingCopilotCli;

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

    // Qwen (DashScope) models
    private static readonly string[] _qwenDefaults =
    [
        "qwen-plus",
        "qwen-max",
        "qwen-turbo",
        "qwen-long",
    ];

    [ObservableProperty]
    private ObservableCollection<string> _qwenModels = [];

    [ObservableProperty]
    private string _customQwenModel = "";

    [RelayCommand]
    private void AddCustomQwenModel()
    {
        var m = CustomQwenModel.Trim();
        if (string.IsNullOrEmpty(m)) return;
        if (!QwenModels.Contains(m, StringComparer.OrdinalIgnoreCase))
        {
            QwenModels.Add(m);
            _settingsService.Settings.QwenModels = [.. QwenModels];
        }
        QwenModel = m;
        CustomQwenModel = "";
    }

    [RelayCommand]
    private void RemoveQwenModel()
    {
        if (string.IsNullOrEmpty(QwenModel)) return;
        var toRemove = QwenModel;
        var idx = QwenModels.IndexOf(toRemove);
        QwenModels.Remove(toRemove);
        _settingsService.Settings.QwenModels = [.. QwenModels];
        if (QwenModels.Count > 0)
            QwenModel = QwenModels[Math.Max(0, idx - 1)];
    }

    [RelayCommand]
    private async Task FetchQwenModelsAsync()
    {
        if (string.IsNullOrWhiteSpace(QwenApiKey))
        {
            StatusText = "Enter Qwen API key first.";
            return;
        }

        IsFetchingModels = true;
        StatusText = "Fetching models from Qwen...";

        try
        {
            var models = await _translationService.FetchQwenModelsAsync(QwenApiKey);

            var merged = QwenModels
                .Concat(models)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(m => m)
                .ToList();

            QwenModels = new ObservableCollection<string>(merged);
            _settingsService.Settings.QwenModels = merged;

            if (merged.Count > 0 && !merged.Contains(QwenModel, StringComparer.OrdinalIgnoreCase))
                QwenModel = merged[0];

            StatusText = $"Found {models.Count} Qwen models ({merged.Count} total).";
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
            AiProvider.Qwen           => "Qwen",
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
            AiProvider.Qwen           => "Qwen",
            _ => "OpenAI"
        };
        OpenAiApiKey = s.OpenAiApiKey;
        GitHubCopilotApiKey = s.GitHubCopilotApiKey;
        OpenRouterApiKey = s.OpenRouterApiKey;
        GeminiApiKey = s.GeminiApiKey;
        DeepSeekApiKey = s.DeepSeekApiKey;
        GroqApiKey = s.GroqApiKey;
        QwenApiKey = s.QwenApiKey;
        OpenAiModel = s.OpenAiModel;
        GitHubCopilotModel = s.GitHubCopilotModel;
        OpenRouterModel = s.OpenRouterModel;
        GeminiModel = s.GeminiModel;
        DeepSeekModel = s.DeepSeekModel;
        GroqModel = s.GroqModel;
        QwenModel = s.QwenModel;
        ChatOpenAiModel = s.ChatOpenAiModel;
        ChatGitHubCopilotModel = s.ChatGitHubCopilotModel;
        ChatOpenRouterModel = s.ChatOpenRouterModel;
        ChatGeminiModel = s.ChatGeminiModel;
        ChatDeepSeekModel = s.ChatDeepSeekModel;
        ChatGroqModel = s.ChatGroqModel;
        ChatOllamaLmStudioModel = s.ChatOllamaLmStudioModel;
        ChatQwenModel = s.ChatQwenModel;
        OllamaLmStudioEndpoint = s.OllamaLmStudioEndpoint;
        OllamaLmStudioModel = s.OllamaLmStudioModel;
        OpenRouterAutoRotate = s.OpenRouterAutoRotate;
        // Load GitHub endpoint label from saved URL
        var urlIdx = Array.IndexOf(_ghEndpointUrls, s.GitHubCopilotInferenceUrl);
        GitHubCopilotEndpointLabel = _ghEndpointLabels[urlIdx >= 0 ? urlIdx : 0];
        GitHubCopilotCliPath = s.GitHubCopilotCliPath;
        _translationService.CopilotCli.CliPathOverride = s.GitHubCopilotCliPath;
        // Populate dropdowns from saved model lists.
        // "Fetch models" replaces the Copilot list with the live catalog, so the hardcoded
        // defaults are only a fallback for an empty list (not merged back in on every start).
        var mergedOnLoad = (s.GitHubCopilotModels.Count > 0 || IsGitHubCopilotCliMode
                ? s.GitHubCopilotModels
                : [.. _ghCopilotDefaults])
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
        // Qwen models
        var mergedQwen = s.QwenModels
            .Concat(_qwenDefaults)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(m => m)
            .ToList();
        QwenModels = new ObservableCollection<string>(mergedQwen);
        _settingsService.Settings.QwenModels = mergedQwen;
        var mergedOllama = s.OllamaModels
            .Concat(new[] { s.OllamaLmStudioModel, s.ChatOllamaLmStudioModel })
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
        _loadingSettings = false;
    }

    private static AiProvider ParseProvider(string label) => label switch
    {
        "GitHub Copilot"        => AiProvider.GitHubCopilot,
        "OpenRouter"            => AiProvider.OpenRouter,
        "Gemini"                => AiProvider.Gemini,
        "DeepSeek"              => AiProvider.DeepSeek,
        "xAI"                   => AiProvider.Groq,
        "llama.cpp / LM Studio" => AiProvider.OllamaLmStudio,
        "Qwen"                  => AiProvider.Qwen,
        _ => AiProvider.OpenAI
    };

    // ──────────────────────────────────────────────────────
    //  Dropdown auto-save — plain selection fields (no free-text typing involved)
    //  persist immediately on change instead of waiting for the Save button.
    // ──────────────────────────────────────────────────────

    partial void OnSelectedThemeChanged(string value)
    {
        _themeService.ApplyTheme(value);
        if (_loadingSettings) return;
        _settingsService.Settings.ThemeName = value;
        _settingsService.Save();
    }

    partial void OnSelectedProviderChanged(string value)
    {
        if (_loadingSettings) return;
        _settingsService.Settings.Provider = ParseProvider(value);
        _settingsService.Save();
    }

    partial void OnSelectedChatProviderChanged(string value)
    {
        if (_loadingSettings) return;
        _settingsService.Settings.ChatProvider = ParseProvider(value);
        _settingsService.Save();
    }

    partial void OnOpenAiModelChanged(string value)
    {
        if (_loadingSettings) return;
        _settingsService.Settings.OpenAiModel = value;
        _settingsService.Save();
    }

    partial void OnGitHubCopilotModelChanged(string value)
    {
        if (_loadingSettings) return;
        _settingsService.Settings.GitHubCopilotModel = value;
        _settingsService.Save();
    }

    partial void OnGitHubCopilotEndpointLabelChanged(string value)
    {
        if (_loadingSettings) return;
        _settingsService.Settings.GitHubCopilotInferenceUrl = SelectedGitHubEndpointUrl;
        _settingsService.Save();

        // Switching to the CLI: pull its live model catalog right away, since the API-era list
        // may contain models the CLI doesn't know.
        if (IsGitHubCopilotCliMode)
            FetchGitHubModelsCommand.Execute(null);
    }

    partial void OnGitHubCopilotCliPathChanged(string value)
    {
        if (_loadingSettings) return;
        _settingsService.Settings.GitHubCopilotCliPath = value.Trim();
        _translationService.CopilotCli.CliPathOverride = value.Trim();
        _settingsService.Save();
    }

    [RelayCommand]
    private async Task CheckCopilotCliAsync()
    {
        IsCheckingCopilotCli = true;
        GitHubCopilotCliStatus = "Starting GitHub Copilot CLI...";
        try
        {
            // Pick up a changed CLI path or a fresh "copilot login".
            await _translationService.CopilotCli.ResetAsync();
            var st = await _translationService.CopilotCli.GetStatusAsync();
            GitHubCopilotCliStatus = st.IsAuthenticated
                ? $"✓ Copilot CLI {st.Version} — signed in as {st.Login}\n{st.CliPath}"
                : $"Copilot CLI {st.Version} found, but not signed in. Run \"copilot login\" in a terminal.\n{st.CliPath}";
        }
        catch (Exception ex)
        {
            GitHubCopilotCliStatus = $"Copilot CLI error: {ex.Message}";
        }
        finally
        {
            IsCheckingCopilotCli = false;
        }
    }

    partial void OnGeminiModelChanged(string value)
    {
        if (_loadingSettings) return;
        _settingsService.Settings.GeminiModel = value;
        _settingsService.Save();
    }

    partial void OnDeepSeekModelChanged(string value)
    {
        if (_loadingSettings) return;
        _settingsService.Settings.DeepSeekModel = value;
        _settingsService.Save();
    }

    partial void OnGroqModelChanged(string value)
    {
        if (_loadingSettings) return;
        _settingsService.Settings.GroqModel = value;
        _settingsService.Save();
    }

    partial void OnQwenModelChanged(string value)
    {
        if (_loadingSettings) return;
        _settingsService.Settings.QwenModel = value;
        _settingsService.Save();
    }

    partial void OnOpenRouterModelChanged(string value)
    {
        if (_loadingSettings) return;
        _settingsService.Settings.OpenRouterModel = value;
        _settingsService.Save();
    }

    partial void OnChatOpenAiModelChanged(string value)
    {
        if (_loadingSettings) return;
        _settingsService.Settings.ChatOpenAiModel = value;
        _settingsService.Save();
    }

    partial void OnChatGitHubCopilotModelChanged(string value)
    {
        if (_loadingSettings) return;
        _settingsService.Settings.ChatGitHubCopilotModel = value;
        _settingsService.Save();
    }

    partial void OnChatGeminiModelChanged(string value)
    {
        if (_loadingSettings) return;
        _settingsService.Settings.ChatGeminiModel = value;
        _settingsService.Save();
    }

    partial void OnChatDeepSeekModelChanged(string value)
    {
        if (_loadingSettings) return;
        _settingsService.Settings.ChatDeepSeekModel = value;
        _settingsService.Save();
    }

    partial void OnChatGroqModelChanged(string value)
    {
        if (_loadingSettings) return;
        _settingsService.Settings.ChatGroqModel = value;
        _settingsService.Save();
    }

    partial void OnChatOpenRouterModelChanged(string value)
    {
        if (_loadingSettings) return;
        _settingsService.Settings.ChatOpenRouterModel = value;
        _settingsService.Save();
    }

    partial void OnChatQwenModelChanged(string value)
    {
        if (_loadingSettings) return;
        _settingsService.Settings.ChatQwenModel = value;
        _settingsService.Save();
    }

    partial void OnDefaultLanguageChanged(string value)
    {
        if (_loadingSettings) return;
        _settingsService.Settings.DefaultLanguage = value;
        _settingsService.Save();
    }

    partial void OnSpeechSourceLanguageChanged(string value)
    {
        if (_loadingSettings) return;
        _settingsService.Settings.SpeechSourceLanguage = value;
        _settingsService.Save();
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
        if (IsGitHubCopilotCliMode)
        {
            await FetchGitHubModelsFromCliAsync();
            return;
        }

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
            if (models.Count == 0)
            {
                StatusText = "GitHub Copilot returned no chat models.";
                return;
            }

            // Replace (not merge) so models that no longer work in chat drop out of the list.
            var translationModel = GitHubCopilotModel;
            var chatModel = ChatGitHubCopilotModel;

            GitHubCopilotModels = new ObservableCollection<string>(models);
            _settingsService.Settings.GitHubCopilotModels = models;

            GitHubCopilotModel = PickAvailable(models, translationModel);
            ChatGitHubCopilotModel = PickAvailable(models, chatModel);

            StatusText = $"Found {models.Count} GitHub Copilot chat models.";
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

    /// <summary>
    /// Replaces the model list with the Copilot CLI's live catalog — exactly what the signed-in
    /// account can use right now, so stale entries are dropped rather than merged.
    /// </summary>
    private async Task FetchGitHubModelsFromCliAsync()
    {
        IsFetchingModels = true;
        StatusText = "Fetching models from GitHub Copilot CLI...";

        try
        {
            var models = await _translationService.CopilotCli.ListModelsAsync();
            if (models.Count == 0)
            {
                StatusText = "GitHub Copilot CLI returned no models.";
                return;
            }

            var translationModel = GitHubCopilotModel;
            var chatModel = ChatGitHubCopilotModel;

            GitHubCopilotModels = new ObservableCollection<string>(models);
            _settingsService.Settings.GitHubCopilotModels = models;

            GitHubCopilotModel = PickAvailable(models, translationModel);
            ChatGitHubCopilotModel = PickAvailable(models, chatModel);

            StatusText = $"Found {models.Count} models in GitHub Copilot CLI.";
        }
        catch (Exception ex)
        {
            StatusText = $"Error fetching models from Copilot CLI: {ex.Message}";
        }
        finally
        {
            IsFetchingModels = false;
        }
    }

    private static string PickAvailable(System.Collections.Generic.List<string> models, string current) =>
        models.FirstOrDefault(m => string.Equals(m, current, StringComparison.OrdinalIgnoreCase))
        ?? models.FirstOrDefault(m => m == "auto")
        ?? models[0];

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

            // Replace (don't merge): the dropdown must only show currently active models,
            // not every model that was ever fetched or configured before.
            var merged = models
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

            StatusText = $"Found {merged.Count} active llama.cpp / LM Studio model(s).";
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
        s.Provider = ParseProvider(SelectedProvider);
        s.OpenAiApiKey = OpenAiApiKey;
        s.GitHubCopilotApiKey = GitHubCopilotApiKey;
        s.OpenRouterApiKey = OpenRouterApiKey;
        s.GeminiApiKey = GeminiApiKey;
        s.DeepSeekApiKey = DeepSeekApiKey;
        s.GroqApiKey = GroqApiKey;
        s.QwenApiKey = QwenApiKey;
        s.OpenAiModel = OpenAiModel;
        s.GitHubCopilotModel = GitHubCopilotModel;
        s.OpenRouterModel = OpenRouterModel;
        s.GeminiModel = GeminiModel;
        s.DeepSeekModel = DeepSeekModel;
        s.GroqModel = GroqModel;
        s.QwenModel = QwenModel;
        s.GroqModels = [.. GroqModels];
        s.QwenModels = [.. QwenModels];
        s.OllamaModels = [.. OllamaModels];
        s.ChatProvider = ParseProvider(SelectedChatProvider);
        s.ChatOpenAiModel = ChatOpenAiModel;
        s.ChatGitHubCopilotModel = ChatGitHubCopilotModel;
        s.ChatOpenRouterModel = ChatOpenRouterModel;
        s.ChatGeminiModel = ChatGeminiModel;
        s.ChatDeepSeekModel = ChatDeepSeekModel;
        s.ChatGroqModel = ChatGroqModel;
        s.ChatOllamaLmStudioModel = ChatOllamaLmStudioModel;
        s.ChatQwenModel = ChatQwenModel;
        s.OllamaLmStudioEndpoint = OllamaLmStudioEndpoint;
        s.OllamaLmStudioModel = OllamaLmStudioModel;
        s.OpenRouterAutoRotate = OpenRouterAutoRotate;
        // GitHub Copilot inference endpoint
        s.GitHubCopilotInferenceUrl = SelectedGitHubEndpointUrl;
        s.GitHubCopilotCliPath = GitHubCopilotCliPath.Trim();
        s.GitHubCopilotModels = [.. GitHubCopilotModels];
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
