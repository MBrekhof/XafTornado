using System;
using System.Collections.Generic;
using LlmTornado;
using LlmTornado.Code;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace XafTornado.Module.Services
{
    /// <summary>
    /// The one process-wide piece of the AI stack: the <see cref="TornadoApi"/> built from the
    /// configured provider keys. Everything with per-user state lives in the scoped
    /// <see cref="AIChatService"/> (SEC-002).
    /// </summary>
    public sealed class TornadoApiProvider
    {
        private readonly Lazy<TornadoApi> _api;

        public TornadoApiProvider(IOptions<AIOptions> optionsAccessor, ILogger<TornadoApiProvider> logger)
        {
            var options = optionsAccessor?.Value ?? new AIOptions();
            _api = new Lazy<TornadoApi>(() =>
            {
                var providerKeys = new List<ProviderAuthentication>();
                foreach (var (providerId, apiKey) in options.ApiKeys)
                {
                    if (string.IsNullOrWhiteSpace(apiKey)) continue;
                    var provider = MapProvider(providerId);
                    if (provider != null)
                        providerKeys.Add(new ProviderAuthentication(provider.Value, apiKey));
                }

                if (providerKeys.Count == 0)
                    throw new InvalidOperationException(
                        "No API keys configured. Add at least one provider key to AI:ApiKeys in appsettings.json.");

                logger.LogInformation("[TornadoInit] Initialized with {Count} providers", providerKeys.Count);
                return new TornadoApi(providerKeys);
            });
        }

        /// <summary>Lazily initialised on first use; throws when no provider key is configured.</summary>
        public TornadoApi Api => _api.Value;

        public static LLmProviders? MapProvider(string providerId) => providerId?.ToLowerInvariant() switch
        {
            "anthropic" => LLmProviders.Anthropic,
            "openai" => LLmProviders.OpenAi,
            "google" => LLmProviders.Google,
            "mistral" => LLmProviders.Mistral,
            "cohere" => LLmProviders.Cohere,
            "voyage" => LLmProviders.Voyage,
            "upstage" => LLmProviders.Upstage,
            _ => null
        };
    }
}
