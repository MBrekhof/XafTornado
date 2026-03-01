using System;
using System.Collections.Generic;

namespace XafTornado.Module.Services
{
    public sealed class AIOptions
    {
        public const string SectionName = "AI";

        /// <summary>Default model ID (e.g. "claude-sonnet-4-6", "gpt-4o").</summary>
        public string Model { get; set; } = "claude-sonnet-4-6";

        /// <summary>Default provider if not derivable from model name.</summary>
        public string DefaultProvider { get; set; } = "anthropic";

        /// <summary>Per-provider API keys. Key = provider ID, Value = API key.</summary>
        public Dictionary<string, string> ApiKeys { get; set; } = new();

        /// <summary>Max output tokens for LLM responses.</summary>
        public int MaxOutputTokens { get; set; } = 16384;

        /// <summary>Max tool-calling iterations per message.</summary>
        public int MaxToolIterations { get; set; } = 10;

        /// <summary>Request timeout in seconds.</summary>
        public int TimeoutSeconds { get; set; } = 120;
    }
}
