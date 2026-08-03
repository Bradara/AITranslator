using System.Collections.Generic;

namespace AITrans.Models;

public enum AiProvider
{
    OpenAI,
    GitHubCopilot,
    OpenRouter,
    Gemini,
    DeepSeek,
    Groq,
    OllamaLmStudio,
    Nvidia
}

public class AppSettings
{
    public AiProvider Provider { get; set; } = AiProvider.OpenAI;
    public AiProvider ChatProvider { get; set; } = AiProvider.OpenAI;

    public string ThemeName { get; set; } = "System";

    // Ebook import
    public string EbookWorkingFolder { get; set; } = "";

    public string OpenAiApiKey { get; set; } = "";
    // Not a pasted PAT: GitHub Copilot's token-exchange endpoint rejects personal access tokens.
    // This holds the long-lived access token obtained through the GitHub OAuth device flow
    // ("Sign in with GitHub" in Settings), which TranslationService exchanges for a short-lived
    // session token before every api.githubcopilot.com call.
    public string GitHubCopilotApiKey { get; set; } = "";
    public string OpenRouterApiKey { get; set; } = "";
    public string GeminiApiKey { get; set; } = "";
    public string DeepSeekApiKey { get; set; } = "";
    public string GroqApiKey { get; set; } = "";
    // llama.cpp / LM Studio: no API key required unless the local server enforces one
    public string OllamaLmStudioApiKey { get; set; } = "";
    public string NvidiaApiKey { get; set; } = "";

    public string OpenAiModel { get; set; } = "gpt-4o-mini";
    public string GitHubCopilotModel { get; set; } = "gpt-4o";
    public string OpenRouterModel { get; set; } = "google/gemini-2.0-flash-exp:free";
    public string GeminiModel { get; set; } = "gemini-2.0-flash";
    public string DeepSeekModel { get; set; } = "deepseek-chat";
    public string GroqModel { get; set; } = "llama-3.3-70b-versatile";
    public string OllamaLmStudioModel { get; set; } = "llama3";
    // Endpoint URL for llama.cpp or LM Studio (OpenAI-compatible)
    public string OllamaLmStudioEndpoint { get; set; } = "http://127.0.0.1:8080/v1/chat/completions";
    public string NvidiaModel { get; set; } = "meta/llama-4-maverick-17b-128e-instruct";
    public List<string> OllamaModels { get; set; } = ["llama3"];

    // Chat (AI Assistant) — per-provider model selection
    public string ChatOpenAiModel { get; set; } = "gpt-4o-mini";
    public string ChatGitHubCopilotModel { get; set; } = "gpt-4o";
    public string ChatOpenRouterModel { get; set; } = "google/gemini-2.0-flash-exp:free";
    public string ChatGeminiModel { get; set; } = "gemini-2.0-flash";
    public string ChatDeepSeekModel { get; set; } = "deepseek-chat";
    public string ChatGroqModel { get; set; } = "llama-3.3-70b-versatile";
    public string ChatOllamaLmStudioModel { get; set; } = "llama3";
    public string ChatNvidiaModel { get; set; } = "meta/llama-4-maverick-17b-128e-instruct";

    public bool OpenRouterAutoRotate { get; set; } = true;

    // claude-* / gemini-* models are not exposed by api.githubcopilot.com/models (Copilot Chat
    // UI only), so the persisted default list only carries the always-available baseline —
    // use "Fetch models" in Settings to pull whatever the account actually has access to.
    public List<string> GitHubCopilotModels { get; set; } =
    [
        "gpt-4o", "gpt-4o-mini",
    ];

    public List<string> GroqModels { get; set; } =
    [
        "llama-3.3-70b-versatile",
        "llama-3.1-8b-instant",
        "gemma2-9b-it",
        "mixtral-8x7b-32768",
    ];

    public List<string> OpenRouterFreeModels { get; set; } =
    [
        "google/gemini-2.0-flash-exp:free",
        "meta-llama/llama-4-maverick:free",
        "qwen/qwen3-235b-a22b:free",
        "mistralai/mistral-small-3.1-24b-instruct:free",
        "google/gemma-3-27b-it:free"
    ];

    // DeepL
    public string DeepLApiKey { get; set; } = "";
    public bool DeepLFreeApi { get; set; } = true;
    public bool UseDeepLForMarkdown { get; set; } = false;

    // Azure AI Translator (Foundry / Cognitive Services)
    public string AzureTranslatorApiKey { get; set; } = "";
    public string AzureTranslatorEndpoint { get; set; } = "https://api.cognitive.microsofttranslator.com";
    public string AzureTranslatorRegion { get; set; } = "";
    public bool UseAzureTranslatorForMarkdown { get; set; } = false;

    // Google Translate (free, unofficial endpoint — no API key required)
    public bool UseGoogleTranslateForMarkdown { get; set; } = false;

    // Azure Speech
    public string AzureSpeechApiKey { get; set; } = "";
    public string AzureSpeechRegion { get; set; } = "";
    public string SpeechSourceLanguage { get; set; } = "English";

    // Markdown preview session
    public string LastPreviewFilePath { get; set; } = "";
    public List<string> RecentPreviewFiles { get; set; } = [];
    public Dictionary<string, int> PreviewLastReadParagraphByFile { get; set; } = [];
    public Dictionary<string, double> PreviewLastScrollRatioByFile { get; set; } = [];  // kept for backwards compat, no longer used for restore
    public Dictionary<string, double> PreviewLastScrollOffsetByFile { get; set; } = [];  // absolute pixel offset
    public Dictionary<string, int> PreviewLastPageByFile { get; set; } = [];

    // Translation progress by file/session
    public Dictionary<string, int> MarkdownLastTranslatedIndexByFile { get; set; } = [];
    public Dictionary<string, int> SubtitlesLastTranslatedIndexByFile { get; set; } = [];
    public Dictionary<string, int> MarkdownLastSelectedIndexByFile { get; set; } = [];
    public Dictionary<string, int> SubtitlesLastSelectedIndexByFile { get; set; } = [];

    public string DefaultLanguage { get; set; } = "Bulgarian";

    public int BatchSize { get; set; } = 30;
    public int MarkdownBatchSize { get; set; } = 10;
    public int DelayBetweenRequestsMs { get; set; } = 3000;
    public double Temperature { get; set; } = 1.0;

    public string ActiveApiKey => GetProviderApiKey(Provider);

    /// <summary>True when the active translation provider needs a non-empty API key to work.</summary>
    public bool ActiveProviderRequiresApiKey => Provider is not AiProvider.OllamaLmStudio;

    public string ActiveModel => Provider switch
    {
        AiProvider.GitHubCopilot => GitHubCopilotModel,
        AiProvider.OpenRouter => OpenRouterModel,
        AiProvider.Gemini => GeminiModel,
        AiProvider.DeepSeek => DeepSeekModel,
        AiProvider.Groq => GroqModel,
        AiProvider.OllamaLmStudio => OllamaLmStudioModel,
        AiProvider.Nvidia => NvidiaModel,
        _ => OpenAiModel
    };

    // GitHub Copilot inference endpoint (selectable in Settings).
    // GitHub Models (models.inference.ai.azure.com) was fully retired on 2026-07-30 —
    // api.githubcopilot.com is the only endpoint still live.
    public string GitHubCopilotInferenceUrl { get; set; } =
        "https://api.githubcopilot.com/chat/completions";

    public string ActiveEndpoint => GetProviderEndpoint(Provider);

    // ── Chat (AI Assistant) active settings ─────────────────────────────────
    // Falls back to the translation provider when the chat-specific key is not set
    // (e.g. fresh install where ChatProvider JSON field doesn't exist yet).

    /// <summary>
    /// The provider actually used for chat. When the explicitly selected ChatProvider
    /// has no API key configured, falls back to the translation Provider.
    /// </summary>
    public AiProvider EffectiveChatProvider
    {
        get
        {
            var chatKey = ChatProvider switch
            {
                AiProvider.GitHubCopilot  => GitHubCopilotApiKey,
                AiProvider.OpenRouter     => OpenRouterApiKey,
                AiProvider.Gemini         => GeminiApiKey,
                AiProvider.DeepSeek       => DeepSeekApiKey,
                AiProvider.Groq           => GroqApiKey,
                AiProvider.OllamaLmStudio => OllamaLmStudioApiKey,
                AiProvider.Nvidia         => NvidiaApiKey,
                _                         => OpenAiApiKey
            };
            // Local providers can work without a key — treat as configured when endpoint is set
            if (ChatProvider == AiProvider.OllamaLmStudio)
                return !string.IsNullOrWhiteSpace(OllamaLmStudioEndpoint) ? ChatProvider : Provider;
            return string.IsNullOrWhiteSpace(chatKey) ? Provider : ChatProvider;
        }
    }

    public string ChatActiveApiKey => GetProviderApiKey(EffectiveChatProvider);

    /// <summary>True when the active chat provider needs a non-empty API key to work.</summary>
    public bool ChatProviderRequiresApiKey => ProviderRequiresApiKey(EffectiveChatProvider);

    public string ChatActiveModel => GetChatModel(EffectiveChatProvider);

    public string ChatActiveEndpoint => GetProviderEndpoint(EffectiveChatProvider);

    // ── Generic per-provider accessors ──────────────────────────────────────
    // Unlike ChatActiveXxx (which always resolves through EffectiveChatProvider,
    // the single app-wide "Chat AI" setting), these accept an arbitrary provider
    // so callers — like the standalone AI Chat tab — can let the user pick any
    // already-configured provider/model independently of that global setting.

    public string GetProviderApiKey(AiProvider provider) => provider switch
    {
        AiProvider.GitHubCopilot  => GitHubCopilotApiKey,
        AiProvider.OpenRouter     => OpenRouterApiKey,
        AiProvider.Gemini         => GeminiApiKey,
        AiProvider.DeepSeek       => DeepSeekApiKey,
        AiProvider.Groq           => GroqApiKey,
        AiProvider.OllamaLmStudio => OllamaLmStudioApiKey,
        AiProvider.Nvidia         => NvidiaApiKey,
        _                         => OpenAiApiKey
    };

    public string GetProviderEndpoint(AiProvider provider) => provider switch
    {
        AiProvider.GitHubCopilot  => GitHubCopilotInferenceUrl,
        AiProvider.OpenRouter     => "https://openrouter.ai/api/v1/chat/completions",
        AiProvider.Gemini         => "https://generativelanguage.googleapis.com/v1beta/models",
        AiProvider.DeepSeek       => "https://api.deepseek.com/chat/completions",
        AiProvider.Groq           => "https://api.x.ai/v1/chat/completions",
        AiProvider.OllamaLmStudio => OllamaLmStudioEndpoint,
        AiProvider.Nvidia         => "https://integrate.api.nvidia.com/v1/chat/completions",
        _                         => "https://api.openai.com/v1/chat/completions"
    };

    /// <summary>True when the given provider needs a non-empty API key to work.</summary>
    public bool ProviderRequiresApiKey(AiProvider provider) => provider != AiProvider.OllamaLmStudio;

    /// <summary>True when the given provider has whatever it needs (API key, or local endpoint) to be usable.</summary>
    public bool IsProviderConfigured(AiProvider provider) =>
        !ProviderRequiresApiKey(provider) || !string.IsNullOrWhiteSpace(GetProviderApiKey(provider));

    /// <summary>The Chat AI model configured in Settings for the given provider.</summary>
    public string GetChatModel(AiProvider provider) => provider switch
    {
        AiProvider.GitHubCopilot  => ChatGitHubCopilotModel,
        AiProvider.OpenRouter     => ChatOpenRouterModel,
        AiProvider.Gemini         => ChatGeminiModel,
        AiProvider.DeepSeek       => ChatDeepSeekModel,
        AiProvider.Groq           => ChatGroqModel,
        AiProvider.OllamaLmStudio => ChatOllamaLmStudioModel,
        AiProvider.Nvidia         => ChatNvidiaModel,
        _                         => ChatOpenAiModel
    };

    /// <summary>
    /// The model choices configured in Settings for the given provider — the user-extensible
    /// picker list where one exists (GitHub Copilot, Groq, OpenRouter, Ollama/LM Studio),
    /// otherwise just the single model configured for that provider.
    /// </summary>
    public List<string> GetChatModelOptions(AiProvider provider) => provider switch
    {
        AiProvider.GitHubCopilot  => GitHubCopilotModels,
        AiProvider.Groq           => GroqModels,
        AiProvider.OpenRouter     => OpenRouterFreeModels,
        AiProvider.OllamaLmStudio => OllamaModels,
        _                         => [GetChatModel(provider)]
    };

    public static string DisplayName(AiProvider provider) => provider switch
    {
        AiProvider.GitHubCopilot  => "GitHub Copilot",
        AiProvider.OpenRouter     => "OpenRouter",
        AiProvider.Gemini         => "Gemini",
        AiProvider.DeepSeek       => "DeepSeek",
        AiProvider.Groq           => "xAI",
        AiProvider.OllamaLmStudio => "llama.cpp / LM Studio",
        AiProvider.Nvidia         => "Nvidia",
        _                         => "OpenAI"
    };
}
