using System.Collections.Generic;

namespace AITrans.Models;

public enum AiProvider
{
    OpenAI,
    GitHubCopilot,
    OpenRouter,
    Gemini
}

public class AppSettings
{
    public AiProvider Provider { get; set; } = AiProvider.OpenAI;

    public string OpenAiApiKey { get; set; } = "";
    public string GitHubCopilotApiKey { get; set; } = "";
    public string OpenRouterApiKey { get; set; } = "";
    public string GeminiApiKey { get; set; } = "";

    public string OpenAiModel { get; set; } = "gpt-4o-mini";
    public string GitHubCopilotModel { get; set; } = "gpt-4o";
    public string OpenRouterModel { get; set; } = "google/gemini-2.0-flash-exp:free";
    public string GeminiModel { get; set; } = "gemini-2.0-flash";

    public bool OpenRouterAutoRotate { get; set; } = true;

    public List<string> GitHubCopilotModels { get; set; } =
    [
        "gpt-4o", "gpt-4o-mini",
        "claude-haiku-4.5", "claude-sonnet-4",
        "gemini-3-flash-preview",
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

    // Azure Speech
    public string AzureSpeechApiKey { get; set; } = "";
    public string AzureSpeechRegion { get; set; } = "";
    public string SpeechSourceLanguage { get; set; } = "English";

    public string DefaultLanguage { get; set; } = "Bulgarian";

    public int BatchSize { get; set; } = 30;
    public int DelayBetweenRequestsMs { get; set; } = 3000;

    public string ActiveApiKey => Provider switch
    {
        AiProvider.GitHubCopilot => GitHubCopilotApiKey,
        AiProvider.OpenRouter => OpenRouterApiKey,
        AiProvider.Gemini => GeminiApiKey,
        _ => OpenAiApiKey
    };

    public string ActiveModel => Provider switch
    {
        AiProvider.GitHubCopilot => GitHubCopilotModel,
        AiProvider.OpenRouter => OpenRouterModel,
        AiProvider.Gemini => GeminiModel,
        _ => OpenAiModel
    };

    public string ActiveEndpoint => Provider switch
    {
         AiProvider.GitHubCopilot => 
        // "https://api.githubcopilot.com/chat/completions",
        // "https://models.inference.ai.azure.com/chat/completions",
"https://models.github.ai/inference/chat/completions",
        AiProvider.OpenRouter => "https://openrouter.ai/api/v1/chat/completions",
        AiProvider.Gemini => "https://generativelanguage.googleapis.com/v1beta/models",
        _ => "https://api.openai.com/v1/chat/completions"
    };
}
