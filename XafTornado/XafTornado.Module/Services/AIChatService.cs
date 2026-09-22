using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LlmTornado;
using LlmTornado.Chat;
using LlmTornado.Chat.Models;
using LlmTornado.ChatFunctions;
using LlmTornado.Code;
using LlmTornado.Common;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Retry;

namespace XafTornado.Module.Services
{
    public sealed class AIChatService : IDisposable
    {
        private readonly AIOptions _options;
        private readonly ILogger<AIChatService> _logger;
        private TornadoApi _api;
        private readonly SemaphoreSlim _initLock = new(1, 1);
        private bool _initialized;

        // Conversation history for continuity across messages
        private readonly List<ChatMessageEntry> _history = new();
        private const int MaxHistoryMessages = 50;

        public string CurrentModel
        {
            get => _options.Model;
            set => _options.Model = value;
        }

        /// <summary>
        /// LLMTornado Tool definitions for the LLM to know what tools are available.
        /// </summary>
        public IReadOnlyList<Tool> TornadoTools { get; set; }

        /// <summary>
        /// AIFunction instances for executing tool calls by name.
        /// </summary>
        public IReadOnlyList<AIFunction> ToolFunctions { get; set; }

        /// <summary>
        /// Produces the system prompt for each turn. A factory rather than a string so the
        /// "current date and time" line is right on day two of an app run (AI-010).
        /// </summary>
        public Func<string> SystemPromptFactory { get; set; }

        /// <summary>One tool invocation made by the model during a turn.</summary>
        public sealed record ToolCall(string Name, string Arguments, string Result);

        // ponytail: singleton-wide "last turn" trace; fine for one dev/test user at a time,
        // make it per-conversation when sessions are isolated (Phase 3 C).
        private List<ToolCall> _lastToolCalls = new();

        /// <summary>Tool calls made during the most recent <see cref="AskAsync"/>, in order. Used by LLM evals.</summary>
        public IReadOnlyList<ToolCall> LastToolCalls => _lastToolCalls;

        public AIChatService(IOptions<AIOptions> optionsAccessor, ILogger<AIChatService> logger)
        {
            _options = optionsAccessor?.Value ?? new AIOptions();
            _logger = logger;
        }

        private void EnsureInitialized()
        {
            if (_initialized) return;
            _initLock.Wait();
            try
            {
                if (_initialized) return;

                var providerKeys = new List<ProviderAuthentication>();
                foreach (var (providerId, apiKey) in _options.ApiKeys)
                {
                    if (string.IsNullOrWhiteSpace(apiKey)) continue;
                    var provider = MapProvider(providerId);
                    if (provider != null)
                        providerKeys.Add(new ProviderAuthentication(provider.Value, apiKey));
                }

                if (providerKeys.Count == 0)
                    throw new InvalidOperationException(
                        "No API keys configured. Add at least one provider key to AI:ApiKeys in appsettings.json.");

                _api = new TornadoApi(providerKeys);
                _initialized = true;
                _logger.LogInformation("[TornadoInit] Initialized with {Count} providers", providerKeys.Count);
            }
            finally
            {
                _initLock.Release();
            }
        }

        public async Task<string> AskAsync(string prompt, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
            EnsureInitialized();
            _lastToolCalls = new List<ToolCall>();

            var provider = ResolveProvider(_options.Model);
            var attempt = new AttemptState();
            var pipeline = CreateRetryPipeline(attempt);
            int toolIterations = 0;

            // AIOptions.TimeoutSeconds bounds the whole turn: every model round-trip plus every tool call (AI-008).
            using var turnCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            if (_options.TimeoutSeconds > 0)
                turnCts.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));

            ChatRichResponse response;
            try
            {
                response = await pipeline.ExecuteAsync(RunTurnAsync, turnCts.Token);
            }
            catch (OperationCanceledException) when (turnCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning("[AskAsync] Turn timed out after {Seconds}s ({Iterations} tool iterations)",
                    _options.TimeoutSeconds, toolIterations);
                // Tools that ran before the timeout may have committed: say so instead of inviting a replay.
                var ran = _lastToolCalls.Select(c => c.Name).Distinct().ToList();
                return ran.Count == 0
                    ? $"The AI request timed out after {_options.TimeoutSeconds} seconds. Please try again."
                    : $"The AI request timed out after {_options.TimeoutSeconds} seconds. {_lastToolCalls.Count} tool call(s) already ran " +
                      $"({string.Join(", ", ran)}); check the data before repeating a create or update.";
            }

            // Build conversation inside the retry lambda so a fresh Conversation is created on each attempt
            // (LlmTornado's Conversation is stateful and may be corrupted after a failure)
            async ValueTask<ChatRichResponse> RunTurnAsync(CancellationToken ct)
            {
                attempt.ToolsRan = false;
                var chatRequest = new ChatRequest
                {
                    Model = new ChatModel(_options.Model, provider),
                    MaxTokens = _options.MaxOutputTokens,
                    Temperature = 1.0
                };

                if (TornadoTools is { Count: > 0 })
                    chatRequest.Tools = TornadoTools.ToList();

                var conversation = _api.Chat.CreateConversation(chatRequest);

                // System prompt
                var systemPrompt = SystemPromptFactory?.Invoke();
                if (!string.IsNullOrWhiteSpace(systemPrompt))
                    conversation.AppendSystemMessage(systemPrompt);

                // Replay conversation history for continuity
                foreach (var entry in _history)
                {
                    if (entry.Role == "user")
                        conversation.AppendUserInput(entry.Content);
                    else
                        conversation.AppendExampleChatbotOutput(entry.Content);
                }

                // Current user message
                conversation.AppendUserInput(prompt);

                _logger.LogInformation("[AskAsync] Sending (model={Model}, provider={Provider}, tools={Tools}, history={History})",
                    _options.Model, provider, TornadoTools?.Count ?? 0, _history.Count);

                // GetResponseRich(fnHandler) populates tool results in the conversation
                // but does NOT automatically re-send to the LLM. We must loop manually:
                // call GetResponseRich(fnHandler), then if tools were called, call
                // GetResponseRich() again on the same conversation (which now includes
                // the tool results as messages) until no more tool calls are returned.
                ChatRichResponse richResponse = null;
                bool hasToolCalls = true;

                while (hasToolCalls && toolIterations < _options.MaxToolIterations)
                {
                    richResponse = await conversation.GetResponseRich(async functionCalls =>
                    {
                        // From here on this attempt may have side effects: never replay it (AI-002).
                        attempt.ToolsRan = true;
                        toolIterations++;
                        _logger.LogInformation("[ToolLoop] Iteration {Iter}: {Count} tool call(s)",
                            toolIterations, functionCalls.Count);

                        foreach (var fc in functionCalls)
                        {
                            ct.ThrowIfCancellationRequested();
                            var result = await ExecuteToolAsync(fc.Name, fc.Arguments ?? "{}", ct);
                            _logger.LogInformation("[ToolLoop] {Name} → {ResultLen} chars", fc.Name, result.Length);
                            fc.Result = new FunctionResult(fc, result);
                        }
                    }, ct);

                    // Check if the response still contains unresolved tool calls
                    hasToolCalls = richResponse?.Blocks?.Any(b =>
                        b.Type == ChatRichResponseBlockTypes.Function && b.FunctionCall != null) == true;

                    if (hasToolCalls)
                        _logger.LogInformation("[ToolLoop] Response still has tool calls, continuing loop");
                }

                if (toolIterations >= _options.MaxToolIterations)
                    _logger.LogWarning("[ToolLoop] Hit max iterations ({Max})", _options.MaxToolIterations);

                return richResponse;
            }

            // Extract text from response
            var finalText = string.Empty;
            if (response?.Blocks != null)
            {
                var textParts = new List<string>();
                foreach (var block in response.Blocks)
                {
                    if (block.Type == ChatRichResponseBlockTypes.Message && block.Message != null)
                        textParts.Add(block.Message);
                }
                finalText = string.Join("\n", textParts);
            }

            // Fallback to simple text property
            if (string.IsNullOrEmpty(finalText) && response != null)
                finalText = response.Text ?? string.Empty;

            // Update conversation history
            _history.Add(new ChatMessageEntry("user", prompt));
            if (!string.IsNullOrEmpty(finalText))
                _history.Add(new ChatMessageEntry("assistant", finalText));

            // Trim history to prevent unbounded growth
            while (_history.Count > MaxHistoryMessages * 2)
            {
                _history.RemoveAt(0);
                _history.RemoveAt(0); // Remove in pairs (user+assistant)
            }

            // Log token usage if available
            if (response?.Usage != null)
            {
                _logger.LogInformation("[AskAsync] Tokens — input: {In}, output: {Out}",
                    response.Usage.PromptTokens, response.Usage.CompletionTokens);
            }

            _logger.LogInformation("[AskAsync] Response: {Len} chars, {Iterations} tool iterations",
                finalText.Length, toolIterations);

            return string.IsNullOrEmpty(finalText)
                ? "No response received from the AI model. Please try again."
                : finalText;
        }

        /// <summary>
        /// Clears conversation history (e.g. when user switches models).
        /// </summary>
        public void ClearHistory() => _history.Clear();

        public async IAsyncEnumerable<string> AskStreamingAsync(
            string prompt,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            // LLMTornado supports streaming but tool calls require the full
            // GetResponseRich() loop. Yield the complete response as one chunk.
            var response = await AskAsync(prompt, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(response))
                yield return response;
        }

        private async Task<string> ExecuteToolAsync(string toolName, string argumentsJson, CancellationToken cancellationToken)
        {
            var result = await ExecuteToolCoreAsync(toolName, argumentsJson, cancellationToken);
            _lastToolCalls.Add(new ToolCall(toolName, argumentsJson, result));
            return result;
        }

        private async Task<string> ExecuteToolCoreAsync(string toolName, string argumentsJson, CancellationToken cancellationToken)
        {
            if (ToolFunctions == null) return "Error: No tools registered.";

            var function = ToolFunctions.FirstOrDefault(f => f.Name == toolName);
            if (function == null) return $"Error: Unknown tool '{toolName}'.";

            try
            {
                var dict = JsonSerializer.Deserialize<Dictionary<string, object>>(argumentsJson)
                    ?? new Dictionary<string, object>();

                var args = new AIFunctionArguments(dict);
                var result = await function.InvokeAsync(args, cancellationToken);
                return result?.ToString() ?? "Tool returned no result.";
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[ExecuteTool] {Name} failed", toolName);
                return $"Error executing {toolName}: {ex.Message}";
            }
        }

        /// <summary>Per-turn state the retry predicate consults; reset at the start of every attempt.</summary>
        private sealed class AttemptState
        {
            public bool ToolsRan;
        }

        private ResiliencePipeline CreateRetryPipeline(AttemptState attempt)
        {
            return new ResiliencePipelineBuilder()
                .AddRetry(new RetryStrategyOptions
                {
                    MaxRetryAttempts = 3,
                    BackoffType = DelayBackoffType.Exponential,
                    Delay = TimeSpan.FromSeconds(2),
                    UseJitter = true,
                    ShouldHandle = new PredicateBuilder().Handle<Exception>(ex =>
                    {
                        // A tool may have committed (create_entity/update_entity): replaying the turn
                        // would let the model do it twice (AI-002). Surface the failure instead.
                        if (attempt.ToolsRan) return false;
                        // User stop or turn timeout is final, not a transient fault (AI-008).
                        if (ex is OperationCanceledException) return false;
                        if (ex is HttpRequestException httpEx)
                        {
                            var status = (int)(httpEx.StatusCode ?? 0);
                            return status == 429 || status >= 500;
                        }
                        return false;
                    }),
                    OnRetry = args =>
                    {
                        _logger.LogWarning(args.Outcome.Exception,
                            "[Retry] Attempt {Attempt}/3 for model {Model}, retrying in {Delay:F1}s",
                            args.AttemptNumber + 1, _options.Model, args.RetryDelay.TotalSeconds);
                        return ValueTask.CompletedTask;
                    }
                })
                .Build();
        }

        private LLmProviders ResolveProvider(string modelId)
        {
            if (modelId.StartsWith("claude", StringComparison.OrdinalIgnoreCase)) return LLmProviders.Anthropic;
            if (modelId.StartsWith("gpt", StringComparison.OrdinalIgnoreCase)) return LLmProviders.OpenAi;
            if (modelId.StartsWith("o3", StringComparison.OrdinalIgnoreCase)) return LLmProviders.OpenAi;
            if (modelId.StartsWith("o4", StringComparison.OrdinalIgnoreCase)) return LLmProviders.OpenAi;
            if (modelId.StartsWith("gemini", StringComparison.OrdinalIgnoreCase)) return LLmProviders.Google;
            if (modelId.StartsWith("mistral", StringComparison.OrdinalIgnoreCase)) return LLmProviders.Mistral;

            return MapProvider(_options.DefaultProvider) ?? LLmProviders.Anthropic;
        }

        private static LLmProviders? MapProvider(string providerId) => providerId?.ToLowerInvariant() switch
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

        public void Dispose()
        {
            _initLock.Dispose();
        }

        private sealed record ChatMessageEntry(string Role, string Content);
    }
}
