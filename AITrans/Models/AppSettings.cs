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

    public List<string> GitHubCopilotModels { get; set; } =
    [
        "gpt-4o", "gpt-4o-mini",
        "claude-haiku-4.5", "claude-sonnet-4",
        "gemini-3-flash-preview",
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

    // DeepL Free (unofficial jsonrpc endpoint — no API key/account required; distinct
    // from DeepLFreeApi above, which toggles the free tier of the *official* DeepL API)
    public bool UseDeepLFreeForMarkdown { get; set; } = false;

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

    public string ActiveApiKey => Provider switch
    {
        AiProvider.GitHubCopilot => GitHubCopilotApiKey,
        AiProvider.OpenRouter => OpenRouterApiKey,
        AiProvider.Gemini => GeminiApiKey,
        AiProvider.DeepSeek => DeepSeekApiKey,
        AiProvider.Groq => GroqApiKey,
        AiProvider.OllamaLmStudio => OllamaLmStudioApiKey,
        AiProvider.Nvidia => NvidiaApiKey,
        _ => OpenAiApiKey
    };

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

    // GitHub Copilot inference endpoint (selectable in Settings)
    public string GitHubCopilotInferenceUrl { get; set; } =
        "https://models.inference.ai.azure.com/chat/completions";

    public string ActiveEndpoint => Provider switch
    {
        AiProvider.GitHubCopilot => GitHubCopilotInferenceUrl,
        AiProvider.OpenRouter => "https://openrouter.ai/api/v1/chat/completions",
        AiProvider.Gemini => "https://generativelanguage.googleapis.com/v1beta/models",
        AiProvider.DeepSeek => "https://api.deepseek.com/chat/completions",
        AiProvider.Groq => "https://api.x.ai/v1/chat/completions",
        AiProvider.OllamaLmStudio => OllamaLmStudioEndpoint,
        AiProvider.Nvidia => "https://integrate.api.nvidia.com/v1/chat/completions",
        _ => "https://api.openai.com/v1/chat/completions"
    };

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

    public string ChatActiveApiKey => EffectiveChatProvider switch
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

    /// <summary>True when the active chat provider needs a non-empty API key to work.</summary>
    public bool ChatProviderRequiresApiKey => EffectiveChatProvider != AiProvider.OllamaLmStudio;

    public string ChatActiveModel => EffectiveChatProvider switch
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

    public string ChatActiveEndpoint => EffectiveChatProvider switch
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
}
