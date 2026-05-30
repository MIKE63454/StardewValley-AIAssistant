using System;
using StardewModdingAPI;

namespace AIAssistant
{
    public enum AIProvider { OpenAI, Claude, Gemini, AzureOpenAI, Custom }

    public enum AITone { Friendly, Professional, Humorous, Warm, Tsundere }

    public class ModConfig
    {
        // Provider
        public AIProvider Provider { get; set; } = AIProvider.OpenAI;
        public string ApiKey { get; set; } = "";
        public string ApiEndpoint { get; set; } = "https://api.openai.com/v1";
        public string Model { get; set; } = "gpt-4o-mini";

        // Behavior
        public bool Enabled { get; set; } = true;
        public string SystemPrompt { get; set; } = "";
        public AITone Tone { get; set; } = AITone.Friendly;
        public bool AutoTone { get; set; } = true;
        public int MaxTokens { get; set; } = 500;
        public float Temperature { get; set; } = 0.8f;
        public string NamePrefix { get; set; } = "[AI]";
        public string TriggerPrefix { get; set; } = "";
        public int HistoryLength { get; set; } = 20;
        public bool DebugMode { get; set; } = false;
        public bool InjectGameContext { get; set; } = true;
        public bool DailyTips { get; set; } = true;
        public SButton ConfigKey { get; set; } = SButton.K;
        public SButton HistoryKey { get; set; } = SButton.C;

        public static ProviderPreset[] Presets => new[]
        {
            new ProviderPreset("OpenAI GPT-4o-mini", AIProvider.OpenAI, "https://api.openai.com/v1", "gpt-4o-mini"),
            new ProviderPreset("OpenAI GPT-4o", AIProvider.OpenAI, "https://api.openai.com/v1", "gpt-4o"),
            new ProviderPreset("Anthropic Claude Sonnet", AIProvider.Claude, "https://api.anthropic.com/v1", "claude-3-5-sonnet-latest"),
            new ProviderPreset("Anthropic Claude Haiku", AIProvider.Claude, "https://api.anthropic.com/v1", "claude-3-5-haiku-latest"),
            new ProviderPreset("Google Gemini Flash", AIProvider.Gemini, "https://generativelanguage.googleapis.com/v1beta", "gemini-2.0-flash"),
            new ProviderPreset("Google Gemini Pro", AIProvider.Gemini, "https://generativelanguage.googleapis.com/v1beta", "gemini-2.5-pro"),
            new ProviderPreset("Azure OpenAI", AIProvider.AzureOpenAI, "https://{res}.openai.azure.com/openai/deployments/{dep}", "gpt-4o-mini"),
            new ProviderPreset("DeepSeek", AIProvider.OpenAI, "https://api.deepseek.com/v1", "deepseek-chat"),
            new ProviderPreset("Groq", AIProvider.OpenAI, "https://api.groq.com/openai/v1", "llama-3.1-8b-instant"),
            new ProviderPreset("Ollama (本地)", AIProvider.Custom, "http://localhost:11434/v1", "llama3"),
            new ProviderPreset("LM Studio (本地)", AIProvider.Custom, "http://localhost:1234/v1", "local-model"),
        };
    }

    public class ProviderPreset
    {
        public string Name { get; set; }
        public AIProvider Provider { get; set; }
        public string Endpoint { get; set; }
        public string Model { get; set; }

        public ProviderPreset(string name, AIProvider provider, string endpoint, string model)
        {
            Name = name; Provider = provider; Endpoint = endpoint; Model = model;
        }
    }
}
