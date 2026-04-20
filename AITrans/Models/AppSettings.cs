using System.Collections.Generic;

namespace AITrans.Models;

public enum AiProvider
{
    OpenAI,
    GitHubCopilot,
    OpenRouter,
    Gemini,
    DeepSeek,
    Groq
}

public class AppSettings
{
    public AiProvider Provider { get; set; } = AiProvider.OpenAI;

    public string ThemeName { get; set; } = "System";

    // Ebook import
    public string EbookWorkingFolder { get; set; } = "";

    public string OpenAiApiKey { get; set; } = "";
    public string GitHubCopilotApiKey { get; set; } = "";
    public string OpenRouterApiKey { get; set; } = "";
    public string GeminiApiKey { get; set; } = "";
    public string DeepSeekApiKey { get; set; } = "";
    public string GroqApiKey { get; set; } = "";

    public string OpenAiModel { get; set; } = "gpt-4o-mini";
    public string GitHubCopilotModel { get; set; } = "gpt-4o";
    public string OpenRouterModel { get; set; } = "google/gemini-2.0-flash-exp:free";
    public string GeminiModel { get; set; } = "gemini-2.0-flash";
    public string DeepSeekModel { get; set; } = "deepseek-chat";
    public string GroqModel { get; set; } = "llama-3.3-70b-versatile";

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
        _ => OpenAiApiKey
    };

    public string ActiveModel => Provider switch
    {
        AiProvider.GitHubCopilot => GitHubCopilotModel,
        AiProvider.OpenRouter => OpenRouterModel,
        AiProvider.Gemini => GeminiModel,
        AiProvider.DeepSeek => DeepSeekModel,
        AiProvider.Groq => GroqModel,
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
        _ => "https://api.openai.com/v1/chat/completions"
    };
}
